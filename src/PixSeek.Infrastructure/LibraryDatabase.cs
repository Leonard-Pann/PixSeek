using System.Text.Json;
using PixSeek.Core;
using Microsoft.Data.Sqlite;

namespace PixSeek.Infrastructure;
public sealed record FrameVector(long Timestamp, string ThumbnailPath, float[] Vector);
public sealed class LibraryDatabase(IPlatformPaths paths) : IVectorSearchService
{
    private readonly SemaphoreSlim _write = new(1, 1);
    private int _dimensions;
    public string VectorStatus { get; private set; } = "Non initialisé";

    public string DatabasePath => Path.Combine(paths.Data, "pixseek.db");

    private string VecPath => Path.Combine(paths.Native, OperatingSystem.IsAndroid() ? "libvec0.so" : OperatingSystem.IsWindows() ? "vec0.dll" : OperatingSystem.IsMacOS() ? "vec0.dylib" : "vec0.so");

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, ForeignKeys = true, DefaultTimeout = 30, Pooling = true }.ToString());
        await c.OpenAsync(ct);
        if (File.Exists(VecPath))
        {
            c.EnableExtensions(true);
            try
            {
                c.LoadExtension(VecPath, "sqlite3_vec_init");
            }
            finally
            {
                c.EnableExtensions(false);
            }
        }

        return c;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await ExecuteAsync(c, "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;", ct);
        await ExecuteAsync(c, """
            CREATE TABLE IF NOT EXISTS SchemaMigration(Version INTEGER PRIMARY KEY, AppliedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS LibraryFolder(Id INTEGER PRIMARY KEY, Path TEXT NOT NULL UNIQUE, DisplayName TEXT NOT NULL, Recursive INTEGER NOT NULL DEFAULT 1, Enabled INTEGER NOT NULL DEFAULT 1, LastScanAt TEXT, CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, Status TEXT NOT NULL DEFAULT 'Prêt');
            CREATE TABLE IF NOT EXISTS MediaFile(Id INTEGER PRIMARY KEY, LibraryFolderId INTEGER NOT NULL REFERENCES LibraryFolder(Id) ON DELETE CASCADE, FullPath TEXT NOT NULL UNIQUE, RelativePath TEXT, FileName TEXT, Extension TEXT, MediaType INTEGER, SizeBytes INTEGER, LastModifiedAt TEXT, FileIdentity TEXT, ContentHash TEXT, Width INTEGER, Height INTEGER, DurationMilliseconds INTEGER, CapturedAt TEXT, CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP, IndexedAt TEXT, ThumbnailPath TEXT, MimeType TEXT, Codec TEXT, Container TEXT, Orientation INTEGER, Status TEXT, ErrorMessage TEXT, IndexedVersion TEXT, Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Media_Folder ON MediaFile(LibraryFolderId);
            CREATE INDEX IF NOT EXISTS IX_Media_Date ON MediaFile(LastModifiedAt DESC,Id DESC);
            CREATE INDEX IF NOT EXISTS IX_Media_Status ON MediaFile(Status,IndexedVersion);
            CREATE TABLE IF NOT EXISTS ModelInfo(Id TEXT PRIMARY KEY, Name TEXT, Version TEXT, EmbeddingDimensions INTEGER NOT NULL, ModelPath TEXT, PreprocessingVersion TEXT, LanguageCapabilities TEXT, Status TEXT);
            CREATE TABLE IF NOT EXISTS VideoFrame(Id INTEGER PRIMARY KEY, MediaFileId INTEGER NOT NULL REFERENCES MediaFile(Id) ON DELETE CASCADE, TimestampMilliseconds INTEGER NOT NULL, ThumbnailPath TEXT, EmbeddingStatus TEXT);
            CREATE TABLE IF NOT EXISTS MediaEmbedding(Id INTEGER PRIMARY KEY, MediaFileId INTEGER NOT NULL REFERENCES MediaFile(Id) ON DELETE CASCADE, VideoFrameId INTEGER REFERENCES VideoFrame(Id) ON DELETE CASCADE, ModelId TEXT NOT NULL, EmbeddingVersion TEXT, Vector BLOB NOT NULL, CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP);
            CREATE INDEX IF NOT EXISTS IX_Embedding_Media ON MediaEmbedding(MediaFileId);
            CREATE TABLE IF NOT EXISTS CompressionJob(Id INTEGER PRIMARY KEY, SourceDirectory TEXT, DestinationDirectory TEXT, Preset TEXT, CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP, StartedAt TEXT, CompletedAt TEXT, Status TEXT, OriginalSizeBytes INTEGER DEFAULT 0, ResultSizeBytes INTEGER DEFAULT 0, SavedBytes INTEGER DEFAULT 0, OptionsJson TEXT);
            CREATE TABLE IF NOT EXISTS CompressionItem(Id INTEGER PRIMARY KEY, JobId INTEGER REFERENCES CompressionJob(Id), SourcePath TEXT, DestinationPath TEXT, SourceCodec TEXT, TargetCodec TEXT, OriginalSizeBytes INTEGER, OutputSizeBytes INTEGER, Status TEXT, ErrorMessage TEXT, VerificationStatus TEXT, Payload TEXT, UNIQUE(JobId,SourcePath));
            INSERT OR IGNORE INTO SchemaMigration VALUES(1,CURRENT_TIMESTAMP);
            UPDATE CompressionJob SET Status='Interrompu' WHERE Status='En cours';
            """, ct);
        // Older upserts updated the JSON record but left the filter columns stale.
        // Repair existing libraries once, without discarding thumbnails or embeddings.
        await ExecuteAsync(c, """
            UPDATE MediaFile SET
                MediaType=CAST(json_extract(Payload,'$.MediaType') AS INTEGER),
                LibraryFolderId=CAST(json_extract(Payload,'$.LibraryFolderId') AS INTEGER),
                FileName=json_extract(Payload,'$.FileName'), Extension=json_extract(Payload,'$.Extension')
            WHERE NOT EXISTS(SELECT 1 FROM SchemaMigration WHERE Version=2);
            CREATE INDEX IF NOT EXISTS IX_Media_Filter ON MediaFile(MediaType,LibraryFolderId,LastModifiedAt DESC,Id DESC);
            INSERT OR IGNORE INTO SchemaMigration VALUES(2,CURRENT_TIMESTAMP);
            """, ct);
        await ExecuteAsync(c, """
            CREATE TABLE IF NOT EXISTS MediaSource(MediaFileId INTEGER NOT NULL REFERENCES MediaFile(Id) ON DELETE CASCADE,LibraryFolderId INTEGER NOT NULL REFERENCES LibraryFolder(Id) ON DELETE CASCADE,PRIMARY KEY(MediaFileId,LibraryFolderId));
            CREATE INDEX IF NOT EXISTS IX_MediaSource_Folder ON MediaSource(LibraryFolderId,MediaFileId);
            INSERT OR IGNORE INTO SchemaMigration VALUES(4,CURRENT_TIMESTAMP);
            """, ct);
        if (File.Exists(VecPath))
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT vec_version()";
            VectorStatus = (string)(await cmd.ExecuteScalarAsync(ct))!;
            cmd.CommandText = "SELECT COALESCE(MAX(EmbeddingDimensions),0) FROM ModelInfo WHERE Status='Actif'";
            _dimensions = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        else
            VectorStatus = $"Extension absente : {VecPath}";
    }

    public async Task ConfigureVectorsAsync(string modelVersion, int dimensions, CancellationToken ct = default, string? modelName = null)
    {
        if (dimensions is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        if (!File.Exists(VecPath))
            throw new FileNotFoundException(VectorStatus);
        await _write.WaitAsync(ct);
        try
        {
            await using var c = await OpenAsync(ct);
            using var check = c.CreateCommand();
            check.CommandText = "SELECT Id,EmbeddingDimensions FROM ModelInfo WHERE Status='Actif'";
            string? old = null;
            int oldDim = 0;
            await using (var reader = await check.ExecuteReaderAsync(ct))
                if (await reader.ReadAsync(ct))
                {
                    old = reader.GetString(0);
                    oldDim = reader.GetInt32(1);
                }

            using var tx = c.BeginTransaction();
            if (old != modelVersion || oldDim != dimensions)
                await ExecuteAsync(c, "DROP TABLE IF EXISTS MediaVector; DELETE FROM MediaEmbedding; DELETE FROM VideoFrame; UPDATE ModelInfo SET Status='Inactif'; UPDATE MediaFile SET IndexedVersion=NULL;", ct, tx);
            await ExecuteAsync(c, $"CREATE VIRTUAL TABLE IF NOT EXISTS MediaVector USING vec0(embedding float[{dimensions}] distance_metric=cosine);", ct, tx);
            using var insert = c.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO ModelInfo(Id,Name,Version,EmbeddingDimensions,PreprocessingVersion,LanguageCapabilities,Status) VALUES($id,$name,$id,$d,$preprocessing,'multilingual','Actif') ON CONFLICT(Id) DO UPDATE SET Status='Actif',Name=$name";
            insert.Parameters.AddWithValue("$name", modelName ?? modelVersion);
            insert.Parameters.AddWithValue("$preprocessing", modelVersion.Split(':').Last());
            insert.Parameters.AddWithValue("$id", modelVersion);
            insert.Parameters.AddWithValue("$d", dimensions);
            await insert.ExecuteNonQueryAsync(ct);
            tx.Commit();
            _dimensions = dimensions;
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task<LibraryFolder> AddFolderAsync(string path, bool recursive, CancellationToken ct = default)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var existing in await FoldersAsync(ct))
        {
            if (string.Equals(existing.Path, path, comparison))
                return existing;
            static string WithSeparator(string p) => Path.EndsInDirectorySeparator(p) ? p : p + Path.DirectorySeparatorChar;
            if (path.StartsWith(WithSeparator(existing.Path), comparison) || existing.Path.StartsWith(WithSeparator(path), comparison))
                throw new InvalidOperationException($"Ce dossier recouvre la bibliothèque « {existing.DisplayName} ». Utilisez le dossier déjà suivi ou retirez-le avant de changer de racine.");
        }

        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO LibraryFolder(Path,DisplayName,Recursive) VALUES($p,$n,$r) ON CONFLICT(Path) DO UPDATE SET Enabled=1 RETURNING Id,Path,DisplayName,Recursive,Status";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$n", Path.GetFileName(path));
        cmd.Parameters.AddWithValue("$r", recursive);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetString(4));
    }

    public async Task<IReadOnlyList<LibraryFolder>> FoldersAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id,Path,DisplayName,Recursive,Status FROM LibraryFolder WHERE Enabled=1 ORDER BY Id";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var items = new List<LibraryFolder>();
        while (await r.ReadAsync(ct))
            items.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetString(4)));
        return items;
    }

    /// <summary>SAF/MediaStore roots are persisted as URIs, never coerced into filesystem paths.</summary>
    public async Task<LibraryFolder> AddContentSourceAsync(string uri, string name, bool recursive, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("content" or "pixseek"))
            throw new ArgumentException("Invalid content source.", nameof(uri));
        await using var c = await OpenAsync(ct);
        using var command = c.CreateCommand();
        command.CommandText = "INSERT INTO LibraryFolder(Path,DisplayName,Recursive) VALUES($p,$n,$r) ON CONFLICT(Path) DO UPDATE SET Enabled=1,DisplayName=$n,Recursive=$r RETURNING Id";
        command.Parameters.AddWithValue("$p", uri); command.Parameters.AddWithValue("$n", name); command.Parameters.AddWithValue("$r", recursive);
        var id = (long)(await command.ExecuteScalarAsync(ct))!;
        return new(id, uri, name, recursive);
    }

    public async Task ReconcileContentSourceAsync(long folderId, IReadOnlySet<string> present, CancellationToken ct)
    {
        // Invoke only after a complete, authorized provider enumeration. A disconnected provider
        // must never mark an entire library absent.
        await _write.WaitAsync(ct);
        try
        {
            await using var c = await OpenAsync(ct);
            using var tx = c.BeginTransaction();
            using var query = c.CreateCommand(); query.Transaction = tx;
            query.CommandText = "SELECT m.Id,m.FullPath FROM MediaFile m JOIN MediaSource s ON s.MediaFileId=m.Id WHERE s.LibraryFolderId=$id";
            query.Parameters.AddWithValue("$id", folderId);
            var absent = new List<long>();
            await using (var reader = await query.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) if (!present.Contains(reader.GetString(1))) absent.Add(reader.GetInt64(0));
            foreach (var id in absent)
            {
                using var unlink = c.CreateCommand(); unlink.Transaction = tx;
                unlink.CommandText = "DELETE FROM MediaSource WHERE MediaFileId=$media AND LibraryFolderId=$folder; SELECT COUNT(*) FROM MediaSource WHERE MediaFileId=$media";
                unlink.Parameters.AddWithValue("$media", id); unlink.Parameters.AddWithValue("$folder", folderId);
                if (Convert.ToInt64(await unlink.ExecuteScalarAsync(ct)) > 0) continue;
                if (_dimensions > 0) await DeleteVectorsAsync(c, id, tx, ct);
                using var update = c.CreateCommand(); update.Transaction = tx;
                update.CommandText = "UPDATE MediaFile SET Status='Absent',IndexedVersion=NULL,Payload=json_set(Payload,'$.Status','Absent','$.IndexedVersion',NULL) WHERE Id=$id";
                update.Parameters.AddWithValue("$id", id); await update.ExecuteNonQueryAsync(ct);
            }
            tx.Commit();
        }
        finally { _write.Release(); }
    }

    public async Task LinkContentMediaAsync(long mediaId, long folderId, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var command = c.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO MediaSource(MediaFileId,LibraryFolderId) VALUES($media,$folder)";
        command.Parameters.AddWithValue("$media", mediaId); command.Parameters.AddWithValue("$folder", folderId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveContentSourceAsync(long folderId, CancellationToken ct)
    {
        // Reassign the owning FK before deleting an overlapping album/gallery source.
        await using (var c = await OpenAsync(ct))
        {
            using var command = c.CreateCommand();
            command.CommandText = """
                UPDATE MediaFile SET LibraryFolderId=(SELECT MIN(s.LibraryFolderId) FROM MediaSource s WHERE s.MediaFileId=MediaFile.Id AND s.LibraryFolderId!=$folder),
                Payload=json_set(Payload,'$.LibraryFolderId',(SELECT MIN(s.LibraryFolderId) FROM MediaSource s WHERE s.MediaFileId=MediaFile.Id AND s.LibraryFolderId!=$folder))
                WHERE LibraryFolderId=$folder AND EXISTS(SELECT 1 FROM MediaSource s WHERE s.MediaFileId=MediaFile.Id AND s.LibraryFolderId!=$folder)
                """;
            command.Parameters.AddWithValue("$folder", folderId); await command.ExecuteNonQueryAsync(ct);
        }
        await RemoveFolderAsync(folderId, ct);
    }

    public async Task SetFolderStatusAsync(long id, string status, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE LibraryFolder SET Status=$s,LastScanAt=CURRENT_TIMESTAMP WHERE Id=$id";
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetFolderRecursiveAsync(long id, bool recursive, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE LibraryFolder SET Recursive=$r WHERE Id=$id";
        cmd.Parameters.AddWithValue("$r", recursive);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<MediaFile?> FindAsync(string path, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id,Payload,IndexedVersion FROM MediaFile WHERE FullPath=$p";
        cmd.Parameters.AddWithValue("$p", path);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadMedia(r) : null;
    }

    private static MediaFile ReadMedia(SqliteDataReader r) => JsonSerializer.Deserialize<MediaFile>(r.GetString(1))!with
    {
        Id = r.GetInt64(0),
        IndexedVersion = r.IsDBNull(2) ? null : r.GetString(2)
    };
    public async Task<MediaFile> SaveMediaAsync(MediaFile media, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO MediaFile(LibraryFolderId,FullPath,FileName,Extension,MediaType,SizeBytes,LastModifiedAt,Width,Height,DurationMilliseconds,ThumbnailPath,Codec,Status,ErrorMessage,IndexedVersion,Payload)
            VALUES($folder,$path,$name,$ext,$type,$size,$date,$w,$h,$duration,$thumb,$codec,$status,$error,$version,$json)
            ON CONFLICT(FullPath) DO UPDATE SET LibraryFolderId=$folder,FileName=$name,Extension=$ext,MediaType=$type,SizeBytes=$size,LastModifiedAt=$date,Width=$w,Height=$h,DurationMilliseconds=$duration,ThumbnailPath=$thumb,Codec=$codec,Status=$status,ErrorMessage=$error,IndexedVersion=$version,Payload=$json RETURNING Id
            """;
        foreach (var(k, v)in new (string, object? )[]
        {
            ("folder", media.LibraryFolderId),
            ("path", media.FullPath),
            ("name", media.FileName),
            ("ext", media.Extension),
            ("type", (int)media.MediaType),
            ("size", media.SizeBytes),
            ("date", media.LastModifiedAt.ToString("O")),
            ("w", media.Width),
            ("h", media.Height),
            ("duration", media.DurationMilliseconds),
            ("thumb", media.ThumbnailPath),
            ("codec", media.Codec),
            ("status", media.Status),
            ("error", media.ErrorMessage),
            ("version", media.IndexedVersion),
            ("json", JsonSerializer.Serialize(media))
        }

        )
            cmd.Parameters.AddWithValue("$" + k, v ?? DBNull.Value);
        return media with
        {
            Id = (long)(await cmd.ExecuteScalarAsync(ct))!
        };
    }

    public async Task UpdateThumbnailAsync(long id, string path, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var command = c.CreateCommand();
        command.CommandText = "UPDATE MediaFile SET ThumbnailPath=$path,Payload=json_set(Payload,'$.ThumbnailPath',$path) WHERE Id=$id";
        command.Parameters.AddWithValue("$path", path); command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<MediaFile>> MediaAsync(int offset = 0, int limit = 240, string type = "Tout", string sort = "Date", long? folder = null, CancellationToken ct = default, DateTime? fromDate = null, DateTime? toDateExclusive = null, long? personId = null, string? faceVersion = null, double faceThreshold = .45)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        var order = sort == "Nom" ? "FileName COLLATE NOCASE" : sort == "Taille" ? "SizeBytes DESC" : "LastModifiedAt DESC";
        cmd.CommandText = $"SELECT m.Id,m.Payload,m.IndexedVersion FROM MediaFile m WHERE m.Status != 'Absent' AND ($type < 0 OR m.MediaType=$type) AND ($folder IS NULL OR m.LibraryFolderId=$folder OR EXISTS(SELECT 1 FROM MediaSource scope WHERE scope.MediaFileId=m.Id AND scope.LibraryFolderId=$folder)) AND ($from IS NULL OR julianday(m.LastModifiedAt)>=julianday($from)) AND ($to IS NULL OR julianday(m.LastModifiedAt)<julianday($to)) {PeopleRepository.FilterSql(personId)} ORDER BY {order},m.Id DESC LIMIT $limit OFFSET $offset";
        PeopleRepository.BindFilter(cmd, personId, faceVersion, faceThreshold);
        cmd.Parameters.AddWithValue("$from", (object? )fromDate?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$to", (object? )toDateExclusive?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$type", type == "Photos" ? 0 : type == "Vidéos" ? 1 : -1);
        cmd.Parameters.AddWithValue("$folder", (object? )folder ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var items = new List<MediaFile>();
        while (await r.ReadAsync(ct))
            items.Add(ReadMedia(r));
        return items;
    }

    public async Task<IReadOnlyList<(long Id, string Path, bool IsVideo)>> SelectionAsync(string type, long? folder, long? personId, string faceVersion, double faceThreshold, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT m.Id,m.FullPath,m.MediaType FROM MediaFile m WHERE m.Status!='Absent' AND ($type<0 OR m.MediaType=$type) AND ($folder IS NULL OR m.LibraryFolderId=$folder OR EXISTS(SELECT 1 FROM MediaSource scope WHERE scope.MediaFileId=m.Id AND scope.LibraryFolderId=$folder)) {PeopleRepository.FilterSql(personId)}";
        cmd.Parameters.AddWithValue("$type", type == "Photos" ? 0 : type == "Vidéos" ? 1 : -1);
        cmd.Parameters.AddWithValue("$folder", (object?)folder ?? DBNull.Value);
        PeopleRepository.BindFilter(cmd, personId, faceVersion, faceThreshold);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var items = new List<(long, string, bool)>();
        while (await reader.ReadAsync(ct)) items.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2) == 1));
        return items;
    }

    public async Task<IReadOnlyList<string>> CompressionDestinationsAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT DestinationDirectory FROM CompressionJob WHERE DestinationDirectory IS NOT NULL";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<string>();
        while (await r.ReadAsync(ct))
            result.Add(r.GetString(0));
        return result;
    }

    public async Task<IReadOnlyList<MediaFile>> FilenameCandidatesAsync(string query, string type = "Tout", long? folder = null, CancellationToken ct = default, long? personId = null, string? faceVersion = null, double faceThreshold = .45)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT m.Id,m.Payload,m.IndexedVersion,m.FileName FROM MediaFile m WHERE m.Status != 'Absent' AND ($type < 0 OR m.MediaType=$type) AND ($folder IS NULL OR m.LibraryFolderId=$folder OR EXISTS(SELECT 1 FROM MediaSource scope WHERE scope.MediaFileId=m.Id AND scope.LibraryFolderId=$folder)) {PeopleRepository.FilterSql(personId)}";
        PeopleRepository.BindFilter(cmd, personId, faceVersion, faceThreshold);
        cmd.Parameters.AddWithValue("$type", type == "Photos" ? 0 : type == "Vidéos" ? 1 : -1);
        cmd.Parameters.AddWithValue("$folder", (object? )folder ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var candidates = new List<(MediaFile Media, double Score)>();
        while (await reader.ReadAsync(ct))
        {
            var score = HybridRanking.NameConfidence(query, reader.GetString(3));
            if (score > 0)
                candidates.Add((ReadMedia(reader), score));
        }

        return candidates.OrderByDescending(x => x.Score).Take(240).Select(x => x.Media).ToArray();
    }

    public async Task<LibraryStats> StatsAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(MediaType=0),0),COALESCE(SUM(MediaType=1),0),(SELECT COUNT(*) FROM LibraryFolder WHERE Enabled=1),COALESCE(SUM(IndexedVersion IS NOT NULL),0),COALESCE(SUM(IndexedVersion IS NULL),0) FROM MediaFile WHERE Status != 'Absent'";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4));
    }

    public static byte[] VectorBytes(ReadOnlySpan<float> vector) => System.Runtime.InteropServices.MemoryMarshal.AsBytes(vector).ToArray();
    public async Task SaveVectorsAsync(MediaFile media, string version, IReadOnlyList<FrameVector> frames, CancellationToken ct)
    {
        if (frames.Count == 0 || frames.Any(f => f.Vector.Length != _dimensions || f.Vector.Any(v => !float.IsFinite(v)) || f.Vector.Sum(v => (double)v * v) < 1e-12))
            throw new ArgumentException("Dimensions ou valeurs du vecteur invalides.");
        await _write.WaitAsync(ct);
        try
        {
            await using var c = await OpenAsync(ct);
            using var tx = c.BeginTransaction();
            await DeleteVectorsAsync(c, media.Id, tx, ct);
            foreach (var frame in frames)
            {
                long? frameId = null;
                if (media.MediaType == MediaType.Video)
                {
                    using var f = c.CreateCommand();
                    f.Transaction = tx;
                    f.CommandText = "INSERT INTO VideoFrame(MediaFileId,TimestampMilliseconds,ThumbnailPath,EmbeddingStatus) VALUES($m,$t,$p,'Indexé') RETURNING Id";
                    f.Parameters.AddWithValue("$m", media.Id);
                    f.Parameters.AddWithValue("$t", frame.Timestamp);
                    f.Parameters.AddWithValue("$p", frame.ThumbnailPath);
                    frameId = (long)(await f.ExecuteScalarAsync(ct))!;
                }

                using var e = c.CreateCommand();
                e.Transaction = tx;
                e.CommandText = "INSERT INTO MediaEmbedding(MediaFileId,VideoFrameId,ModelId,EmbeddingVersion,Vector) VALUES($m,$f,$v,$v,$b) RETURNING Id";
                e.Parameters.AddWithValue("$m", media.Id);
                e.Parameters.AddWithValue("$f", (object? )frameId ?? DBNull.Value);
                e.Parameters.AddWithValue("$v", version);
                e.Parameters.AddWithValue("$b", VectorBytes(frame.Vector));
                var id = (long)(await e.ExecuteScalarAsync(ct))!;
                using var v = c.CreateCommand();
                v.Transaction = tx;
                v.CommandText = "INSERT INTO MediaVector(rowid,embedding) VALUES($id,$b)";
                v.Parameters.AddWithValue("$id", id);
                v.Parameters.AddWithValue("$b", VectorBytes(frame.Vector));
                await v.ExecuteNonQueryAsync(ct);
            }

            using var update = c.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE MediaFile SET IndexedAt=CURRENT_TIMESTAMP,IndexedVersion=$v WHERE Id=$id";
            update.Parameters.AddWithValue("$v", version);
            update.Parameters.AddWithValue("$id", media.Id);
            await update.ExecuteNonQueryAsync(ct);
            tx.Commit();
        }
        finally
        {
            _write.Release();
        }
    }

    private static async Task DeleteVectorsAsync(SqliteConnection c, long id, SqliteTransaction tx, CancellationToken ct)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM MediaVector WHERE rowid IN (SELECT Id FROM MediaEmbedding WHERE MediaFileId=$id); DELETE FROM MediaEmbedding WHERE MediaFileId=$id; DELETE FROM VideoFrame WHERE MediaFileId=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveFolderAsync(long id, CancellationToken ct = default)
    {
        await _write.WaitAsync(ct);
        try
        {
            await using var c = await OpenAsync(ct);
            using var tx = c.BeginTransaction();
            if (_dimensions > 0)
            {
                using var v = c.CreateCommand();
                v.Transaction = tx;
                v.CommandText = "DELETE FROM MediaVector WHERE rowid IN (SELECT e.Id FROM MediaEmbedding e JOIN MediaFile m ON m.Id=e.MediaFileId WHERE m.LibraryFolderId=$id)";
                v.Parameters.AddWithValue("$id", id);
                await v.ExecuteNonQueryAsync(ct);
            }

            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM LibraryFolder WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            tx.Commit();
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task InvalidateVectorsAsync(long id, CancellationToken ct)
    {
        if (_dimensions == 0)
            return;
        await _write.WaitAsync(ct);
        try
        {
            await using var c = await OpenAsync(ct);
            using var tx = c.BeginTransaction();
            await DeleteVectorsAsync(c, id, tx, ct);
            tx.Commit();
        }
        finally
        {
            _write.Release();
        }
    }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(ReadOnlyMemory<float> queryEmbedding, int maxResults, CancellationToken cancellationToken)
        => SearchFilteredAsync(queryEmbedding, maxResults, "Tout", null, cancellationToken);

    public async Task<IReadOnlyList<SearchResult>> SearchFilteredAsync(ReadOnlyMemory<float> queryEmbedding, int maxResults, string type, long? folder, CancellationToken cancellationToken = default, long? personId = null, string? faceVersion = null, double faceThreshold = .45)
    {
        if (maxResults is < 1 or > 512)
            throw new ArgumentOutOfRangeException(nameof(maxResults));
        if (queryEmbedding.Length != _dimensions || _dimensions == 0)
            throw new ArgumentException("Index vectoriel non initialisé ou dimension incompatible.");
        await using var c = await OpenAsync(cancellationToken);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            WITH nearest AS (
                SELECT rowid,distance FROM MediaVector WHERE embedding MATCH $vector AND k=$k
                AND rowid IN (
                    SELECT e.Id FROM MediaEmbedding e JOIN MediaFile m ON m.Id=e.MediaFileId
                    WHERE m.Status != 'Absent' AND m.IndexedVersion IS NOT NULL
                    AND ($type < 0 OR m.MediaType=$type) AND ($folder IS NULL OR m.LibraryFolderId=$folder OR EXISTS(SELECT 1 FROM MediaSource scope WHERE scope.MediaFileId=m.Id AND scope.LibraryFolderId=$folder))
                    {PeopleRepository.FilterSql(personId)}
                )
            )
            SELECT m.Id,m.Payload,m.IndexedVersion,n.distance,f.TimestampMilliseconds,f.ThumbnailPath
            FROM nearest n JOIN MediaEmbedding e ON e.Id=n.rowid JOIN MediaFile m ON m.Id=e.MediaFileId LEFT JOIN VideoFrame f ON f.Id=e.VideoFrameId
            WHERE m.Status != 'Absent' AND m.IndexedVersion IS NOT NULL ORDER BY n.distance
            """;
        cmd.Parameters.AddWithValue("$vector", VectorBytes(queryEmbedding.Span));
        cmd.Parameters.AddWithValue("$type", type == "Photos" ? 0 : type == "Vidéos" ? 1 : -1);
        cmd.Parameters.AddWithValue("$folder", (object?)folder ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$k", Math.Clamp(maxResults * 8, 1, 4096));
        PeopleRepository.BindFilter(cmd, personId, faceVersion, faceThreshold);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<SearchResult>();
        var seen = new HashSet<long>();
        while (await r.ReadAsync(cancellationToken))
        {
            var m = ReadMedia(r);
            if (seen.Add(m.Id))
                items.Add(new(m, 1 - r.GetDouble(3), r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? m.ThumbnailPath : r.GetString(5)));
            if (items.Count >= maxResults)
                break;
        }

        return items;
    }

    public static async Task ExecuteAsync(SqliteConnection c, string sql, CancellationToken ct = default, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
