using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PixSeek.Core;

namespace PixSeek.Infrastructure;

public sealed class PeopleRepository(LibraryDatabase db)
{
    private readonly SemaphoreSlim _write = new(1);
    private readonly SemaphoreSlim _initialization = new(1);
    private bool _ready;
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _initialization.WaitAsync(ct);
        try
        {
            if (_ready) return;
            await using var c = await db.OpenAsync(ct);
            await LibraryDatabase.ExecuteAsync(c, """
                CREATE TABLE IF NOT EXISTS Person(Id INTEGER PRIMARY KEY,CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP);
                CREATE TABLE IF NOT EXISTS PersonAlias(Normalized TEXT PRIMARY KEY,DisplayName TEXT NOT NULL,PersonId INTEGER NOT NULL REFERENCES Person(Id) ON DELETE CASCADE);
                CREATE TABLE IF NOT EXISTS PersonReference(Id INTEGER PRIMARY KEY,PersonId INTEGER NOT NULL REFERENCES Person(Id) ON DELETE CASCADE,ModelVersion TEXT NOT NULL,Vector BLOB NOT NULL,SourcePath TEXT NOT NULL,ThumbnailPath TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS FaceScan(MediaFileId INTEGER PRIMARY KEY REFERENCES MediaFile(Id) ON DELETE CASCADE,Signature TEXT NOT NULL,Status TEXT NOT NULL,Error TEXT);
                CREATE TABLE IF NOT EXISTS FaceObservation(Id INTEGER PRIMARY KEY,MediaFileId INTEGER NOT NULL REFERENCES MediaFile(Id) ON DELETE CASCADE,ModelVersion TEXT NOT NULL,SizeBytes INTEGER NOT NULL,ModifiedAt TEXT NOT NULL,ThumbnailPath TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_Face_Media ON FaceObservation(MediaFileId);
                CREATE VIRTUAL TABLE IF NOT EXISTS FaceVector USING vec0(embedding float[128] distance_metric=cosine);
                INSERT OR IGNORE INTO SchemaMigration VALUES(3,CURRENT_TIMESTAMP);
                """, ct);
            _ready = true;
        }
        finally { _initialization.Release(); }
    }
    public static string Normalize(string text)
    {
        var result = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormD).ToLowerInvariant())
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                result.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return string.Join(' ', result.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
    public static string Signature(MediaFile media, string version) => $"{media.SizeBytes}|{media.LastModifiedAt.Ticks}|{version}";

    public async Task<IReadOnlyList<PersonProfile>> ProfilesAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var c = await db.OpenAsync(ct);
        using var command = c.CreateCommand();
        command.CommandText = "SELECT PersonId,DisplayName FROM PersonAlias ORDER BY PersonId,DisplayName";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var map = new Dictionary<long, List<string>>();
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            if (!map.TryGetValue(id, out var aliases)) map[id] = aliases = [];
            aliases.Add(reader.GetString(1));
        }
        return map.Select(p => new PersonProfile(p.Key, p.Value)).ToArray();
    }

    public async Task<long> AddAsync(IReadOnlyList<string> keywords, IReadOnlyList<DetectedFace> references, string version, CancellationToken ct)
    {
        if (references.Count == 0 || keywords.Count == 0) throw new ArgumentException("Select a face and at least one keyword.");
        var aliases = keywords.Select(s => (Display: s.Trim(), Key: Normalize(s))).Where(p => p.Key.Length > 0).DistinctBy(p => p.Key).ToArray();
        if (aliases.Length == 0 || aliases.Any(p => p.Key.Length > 100)) throw new ArgumentException("Invalid person keywords.");
        if (references.Any(r => r.Vector.Length != 128 || r.Vector.Any(v => !float.IsFinite(v)))) throw new ArgumentException("Invalid face vectors.");
        await InitializeAsync(ct);
        await _write.WaitAsync(ct);
        try
        {
            await using var c = await db.OpenAsync(ct);
            using var tx = c.BeginTransaction();
            var existingIds = new HashSet<long>();
            foreach (var alias in aliases)
            {
                using var check = c.CreateCommand(); check.Transaction = tx;
                check.CommandText = "SELECT PersonId FROM PersonAlias WHERE Normalized=$key";
                check.Parameters.AddWithValue("$key", alias.Key);
                if (await check.ExecuteScalarAsync(ct) is long found) existingIds.Add(found);
            }
            if (existingIds.Count > 1) throw new ArgumentException("These keywords already belong to different people.");
            long id;
            if (existingIds.Count == 1) id = existingIds.Single();
            else
            {
                using var create = c.CreateCommand(); create.Transaction = tx;
                create.CommandText = "INSERT INTO Person DEFAULT VALUES RETURNING Id";
                id = (long)(await create.ExecuteScalarAsync(ct))!;
            }
            foreach (var alias in aliases)
            {
                using var insert = c.CreateCommand(); insert.Transaction = tx;
                insert.CommandText = "INSERT OR IGNORE INTO PersonAlias(Normalized,DisplayName,PersonId) VALUES($key,$display,$id)";
                insert.Parameters.AddWithValue("$key", alias.Key); insert.Parameters.AddWithValue("$display", alias.Display);
                insert.Parameters.AddWithValue("$id", id); await insert.ExecuteNonQueryAsync(ct);
            }
            foreach (var reference in references)
            {
                using var insert = c.CreateCommand(); insert.Transaction = tx;
                insert.CommandText = "INSERT INTO PersonReference(PersonId,ModelVersion,Vector,SourcePath,ThumbnailPath) VALUES($id,$version,$vector,$path,$thumb)";
                insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$version", version);
                insert.Parameters.AddWithValue("$vector", LibraryDatabase.VectorBytes(reference.Vector));
                insert.Parameters.AddWithValue("$path", reference.SourcePath); insert.Parameters.AddWithValue("$thumb", reference.ThumbnailPath);
                await insert.ExecuteNonQueryAsync(ct);
            }
            tx.Commit();
            return id;
        }
        finally { _write.Release(); }
    }

    public async Task DeleteAsync(long personId, CancellationToken ct = default)
    {
        await InitializeAsync(ct); await _write.WaitAsync(ct);
        try
        {
            await using var c = await db.OpenAsync(ct);
            using var command = c.CreateCommand(); command.CommandText = "DELETE FROM Person WHERE Id=$id";
            command.Parameters.AddWithValue("$id", personId); await command.ExecuteNonQueryAsync(ct);
        }
        finally { _write.Release(); }
    }

    public async Task<bool> IsScannedAsync(MediaFile media, string version, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        using var query = c.CreateCommand(); query.CommandText = "SELECT Signature FROM FaceScan WHERE MediaFileId=$id AND Status='Complete'";
        query.Parameters.AddWithValue("$id", media.Id);
        return await query.ExecuteScalarAsync(ct) as string == Signature(media, version);
    }

    public async Task PruneAsync(CancellationToken ct)
    {
        await _write.WaitAsync(ct);
        try
        {
            await using var c = await db.OpenAsync(ct);
            await LibraryDatabase.ExecuteAsync(c, "DELETE FROM FaceVector WHERE rowid NOT IN (SELECT Id FROM FaceObservation)", ct);
        }
        finally { _write.Release(); }
    }

    public async Task SaveFacesAsync(MediaFile media, IReadOnlyList<DetectedFace> faces, string version, CancellationToken ct, string? error = null)
    {
        await _write.WaitAsync(ct);
        try
        {
            await using var c = await db.OpenAsync(ct);
            using var tx = c.BeginTransaction();
            using var exists = c.CreateCommand(); exists.Transaction = tx;
            exists.CommandText = "SELECT EXISTS(SELECT 1 FROM MediaFile WHERE Id=$id)";
            exists.Parameters.AddWithValue("$id", media.Id);
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct)) == 0) return;
            using var clear = c.CreateCommand(); clear.Transaction = tx;
            clear.CommandText = "DELETE FROM FaceVector WHERE rowid IN (SELECT Id FROM FaceObservation WHERE MediaFileId=$id); DELETE FROM FaceObservation WHERE MediaFileId=$id";
            clear.Parameters.AddWithValue("$id", media.Id); await clear.ExecuteNonQueryAsync(ct);
            foreach (var face in faces)
            {
                using var insert = c.CreateCommand(); insert.Transaction = tx;
                insert.CommandText = "INSERT INTO FaceObservation(MediaFileId,ModelVersion,SizeBytes,ModifiedAt,ThumbnailPath) VALUES($id,$version,$size,$date,$thumb) RETURNING Id";
                insert.Parameters.AddWithValue("$id", media.Id); insert.Parameters.AddWithValue("$version", version);
                insert.Parameters.AddWithValue("$size", media.SizeBytes); insert.Parameters.AddWithValue("$date", media.LastModifiedAt.ToString("O"));
                insert.Parameters.AddWithValue("$thumb", face.ThumbnailPath);
                var id = (long)(await insert.ExecuteScalarAsync(ct))!;
                using var vector = c.CreateCommand(); vector.Transaction = tx;
                vector.CommandText = "INSERT INTO FaceVector(rowid,embedding) VALUES($id,$vector)";
                vector.Parameters.AddWithValue("$id", id); vector.Parameters.AddWithValue("$vector", LibraryDatabase.VectorBytes(face.Vector));
                await vector.ExecuteNonQueryAsync(ct);
            }
            using var state = c.CreateCommand(); state.Transaction = tx;
            state.CommandText = "INSERT INTO FaceScan(MediaFileId,Signature,Status,Error) VALUES($id,$signature,$status,$error) ON CONFLICT(MediaFileId) DO UPDATE SET Signature=$signature,Status=$status,Error=$error";
            state.Parameters.AddWithValue("$id", media.Id); state.Parameters.AddWithValue("$signature", Signature(media, version));
            state.Parameters.AddWithValue("$status", error == null ? "Complete" : "Error"); state.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            await state.ExecuteNonQueryAsync(ct); tx.Commit();
        }
        finally { _write.Release(); }
    }

    // Correlated against the outer MediaFile alias. Unlike top-K face search this covers every
    // matching photo, so pagination, select-all and semantic pre-filtering share the same scope.
    internal static string FilterSql(long? personId) => personId.HasValue ? """
        AND EXISTS (
            SELECT 1 FROM FaceObservation face
            JOIN FaceVector fv ON fv.rowid=face.Id
            JOIN PersonReference reference ON reference.PersonId=$person AND reference.ModelVersion=face.ModelVersion
            WHERE face.MediaFileId=m.Id AND face.ModelVersion=$faceVersion
            AND face.SizeBytes=m.SizeBytes AND face.ModifiedAt=m.LastModifiedAt
            AND vec_distance_cosine(fv.embedding,reference.Vector)<=$faceDistance
        )
        """ : "";

    internal static void BindFilter(SqliteCommand command, long? personId, string? version, double threshold)
    {
        if (!personId.HasValue) return;
        command.Parameters.AddWithValue("$person", personId.Value);
        command.Parameters.AddWithValue("$faceVersion", version ?? "");
        command.Parameters.AddWithValue("$faceDistance", 1 - threshold);
    }

    public async Task<IReadOnlyList<PersonSearchMatch>> SearchAsync(string query, string type, long? folder, string version, double threshold, CancellationToken ct = default, long? personId = null)
    {
        await InitializeAsync(ct);
        if (type == "Vidéos") return [];
        var normalized = " " + Normalize(query) + " ";
        var profiles = (await ProfilesAsync(ct)).Where(p => p.Keywords.Any(k => normalized.Contains(" " + Normalize(k) + " ", StringComparison.Ordinal))).ToArray();
        if (profiles.Length == 0) return [];
        await using var c = await db.OpenAsync(ct);
        var matches = new Dictionary<long, (MediaFile Media, double Score, HashSet<long> People)>();
        foreach (var profile in profiles)
        {
            using var refs = c.CreateCommand();
            refs.CommandText = "SELECT Vector FROM PersonReference WHERE PersonId=$id AND ModelVersion=$version";
            refs.Parameters.AddWithValue("$id", profile.Id); refs.Parameters.AddWithValue("$version", version);
            var vectors = new List<byte[]>();
            await using (var reader = await refs.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) vectors.Add((byte[])reader.GetValue(0));
            foreach (var vector in vectors)
            {
                using var search = c.CreateCommand();
                search.CommandText = $"""
                    WITH nearest AS (
                        SELECT rowid,distance FROM FaceVector WHERE embedding MATCH $vector AND k=1920
                        AND rowid IN (
                            SELECT f.Id FROM FaceObservation f JOIN MediaFile m ON m.Id=f.MediaFileId
                            WHERE m.Status!='Absent' AND f.ModelVersion=$version
                            AND f.SizeBytes=m.SizeBytes AND f.ModifiedAt=m.LastModifiedAt
                            AND ($folder IS NULL OR m.LibraryFolderId=$folder)
                            {FilterSql(personId)}
                        )
                    )
                    SELECT m.Id,m.Payload,m.IndexedVersion,n.distance FROM nearest n
                    JOIN FaceObservation f ON f.Id=n.rowid JOIN MediaFile m ON m.Id=f.MediaFileId
                    WHERE n.distance <= $distance ORDER BY n.distance
                    """;
                search.Parameters.AddWithValue("$vector", vector); search.Parameters.AddWithValue("$version", version);
                search.Parameters.AddWithValue("$folder", (object?)folder ?? DBNull.Value); search.Parameters.AddWithValue("$distance", 1 - threshold);
                BindFilter(search, personId, version, threshold);
                await using var reader = await search.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var id = reader.GetInt64(0); var score = 1 - reader.GetDouble(3);
                    if (matches.TryGetValue(id, out var old))
                    {
                        old.People.Add(profile.Id); matches[id] = (old.Media, Math.Max(score, old.Score), old.People);
                    }
                    else
                    {
                        var media = JsonSerializer.Deserialize<MediaFile>(reader.GetString(1))! with { Id = id, IndexedVersion = reader.IsDBNull(2) ? null : reader.GetString(2) };
                        matches[id] = (media, score, [profile.Id]);
                    }
                }
            }
        }
        return matches.Values.OrderByDescending(m => m.People.Count).ThenByDescending(m => m.Score)
            .Take(240).Select(m => new PersonSearchMatch(m.Media, m.Score, m.People.Count)).ToArray();
    }
}
