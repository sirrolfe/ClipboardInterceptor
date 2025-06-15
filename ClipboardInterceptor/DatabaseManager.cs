using System.Data.SQLite;

namespace ClipboardInterceptor
{
    public class DatabaseManager
    {
        private const string DbFileName = "clipboardHistory.db";
        private static readonly string ConnectionString = $"Data Source={DbFileName};Version=3;";
        private static DatabaseManager _instance;
        private static readonly object _lock = new object();

        public static DatabaseManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new DatabaseManager();
                        }
                    }
                }
                return _instance;
            }
        }

        private DatabaseManager()
        {
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            if (!File.Exists(DbFileName))
            {
                SQLiteConnection.CreateFile(DbFileName);

                using (var connection = new SQLiteConnection(ConnectionString))
                {
                    connection.Open();

                    using (var command = new SQLiteCommand(connection))
                    {
                        // Create the table
                        command.CommandText = @"
                            CREATE TABLE ClipboardItems (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                Timestamp TEXT NOT NULL,
                                ItemType INTEGER NOT NULL,
                                EncryptedData TEXT NOT NULL,
                                ContentId TEXT NOT NULL,
                                Preview TEXT,
                                IsSensitive INTEGER NOT NULL,
                                ExpiresAt TEXT
                            );
                            
                            CREATE TABLE Settings (
                                Key TEXT PRIMARY KEY,
                                Value TEXT NOT NULL
                            );
                        ";
                        command.ExecuteNonQuery();
                    }
                }
            }

            // Cleanup expired items on startup
            CleanupExpiredItems();
        }

        public void CleanupExpiredItems()
        {
            using (var connection = new SQLiteConnection(ConnectionString))
            {
                connection.Open();
                using (var command = new SQLiteCommand(connection))
                {
                    command.CommandText = "DELETE FROM ClipboardItems WHERE ExpiresAt IS NOT NULL AND ExpiresAt < @now";
                    command.Parameters.AddWithValue("@now", DateTime.Now.ToString("o"));
                    command.ExecuteNonQuery();
                }
            }
        }

        // UPDATED: Method untuk cleanup berdasarkan kategori dengan retention dinamis
        public void CleanupExpiredItemsByCategory()
        {
            using (var connection = new SQLiteConnection(ConnectionString))
            {
                connection.Open();
                using (var command = new SQLiteCommand(connection))
                {
                    // Get retention settings dari database
                    int sensitiveHours = int.Parse(GetSetting("SensitiveRetention", "3"));
                    int normalHours = int.Parse(GetSetting("NormalRetention", "12"));

                    // Cleanup untuk data sensitif berdasarkan setting
                    command.CommandText = $@"
                        DELETE FROM ClipboardItems 
                        WHERE IsSensitive = 1 
                        AND datetime(Timestamp, '+{sensitiveHours} hours') < datetime('now')";
                    command.ExecuteNonQuery();

                    // Cleanup untuk data normal berdasarkan setting
                    command.CommandText = $@"
                        DELETE FROM ClipboardItems 
                        WHERE IsSensitive = 0 
                        AND datetime(Timestamp, '+{normalHours} hours') < datetime('now')";
                    command.ExecuteNonQuery();

                    // Cleanup items yang sudah expired berdasarkan ExpiresAt
                    command.CommandText = @"
                        DELETE FROM ClipboardItems 
                        WHERE ExpiresAt IS NOT NULL 
                        AND ExpiresAt < datetime('now')";
                    command.ExecuteNonQuery();
                }
            }
        }

        public long SaveClipboardItem(ClipboardItem item)
        {
            using (var connection = new SQLiteConnection(ConnectionString))
            {
                connection.Open();
                using (var command = new SQLiteCommand(connection))
                {
                    command.CommandText = @"
                        INSERT INTO ClipboardItems 
                        (Timestamp, ItemType, EncryptedData, ContentId, Preview, IsSensitive, ExpiresAt) 
                        VALUES 
                        (@timestamp, @itemType, @encryptedData, @contentId, @preview, @isSensitive, @expiresAt);
                        
                        SELECT last_insert_rowid();
                    ";

                    command.Parameters.AddWithValue("@timestamp", item.Timestamp.ToString("o"));
                    command.Parameters.AddWithValue("@itemType", (int)item.ItemType);
                    command.Parameters.AddWithValue("@encryptedData", item.EncryptedData);
                    command.Parameters.AddWithValue("@contentId", item.ContentId);
                    command.Parameters.AddWithValue("@preview", item.Preview ?? "");
                    command.Parameters.AddWithValue("@isSensitive", item.IsSensitive ? 1 : 0);

                    if (item.ExpiresAt.HasValue)
                        command.Parameters.AddWithValue("@expiresAt", item.ExpiresAt.Value.ToString("o"));
                    else
                        command.Parameters.AddWithValue("@expiresAt", DBNull.Value);

                    long newId = (long)command.ExecuteScalar();
                    return newId;
                }
            }
        }

        public List<ClipboardItem> GetRecentItems(int limit = 50)
        {
            var items = new List<ClipboardItem>();

            using (var connection = new SQLiteConnection(ConnectionString))
            {
                connection.Open();
                using (var command = new SQLiteCommand(connection))
                {
                    command.CommandText = @"
                        SELECT Id, Timestamp, ItemType, EncryptedData, ContentId, Preview, IsSensitive, ExpiresAt 
                        FROM ClipboardItems 
                        WHERE (ExpiresAt IS NULL OR ExpiresAt > @now)
                        ORDER BY Timestamp DESC 
                        LIMIT @limit";

                    command.Parameters.AddWithValue("@now", DateTime.Now.ToString("o"));
                    command.Parameters.AddWithValue("@limit", limit);

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var item = new ClipboardItem
                            {
                                Id = reader.GetInt64(0),
                                Timestamp = DateTime.Parse(reader.GetString(1)),
                                ItemType = (ClipboardItemType)reader.GetInt32(2),
                                EncryptedData = reader.GetString(3),
                                ContentId = reader.GetString(4),
                                Preview = reader.GetString(5),
                                IsSensitive = reader.GetInt32(6) == 1
                            };

                            if (!reader.IsDBNull(7))
                            {
                                item.ExpiresAt = DateTime.Parse(reader.GetString(7));
                            }

                            items.Add(item);
                        }
                    }
                }
            }

            return items;
        }

        public ClipboardItem GetItemById(long id)
        {
            using (var connection = new SQLiteConnection(ConnectionString))
            {
                connection.Open();
                using (var command = new SQLiteCommand(connection))
                {
                    command.CommandText = @"
                        SELECT Id, Timestamp, ItemType, EncryptedData, ContentId, Preview, IsSensitive, ExpiresAt 
                        FROM ClipboardItems 
                        WHERE Id = @id";

                    command.Parameters.AddWithValue("@id", id);

                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            var item = new ClipboardItem
                            {
                                Id = reader.GetInt64(0),
                                Timestamp = DateTime.Parse(reader.GetString(1)),
                                ItemType = (ClipboardItemType)reader.GetInt32(2),
                                EncryptedData = reader.GetString(3),
                                ContentId = reader.GetString(4),
                                Preview = reader.GetString(5),
                                IsSensitive = reader.GetInt32(6) == 1
                            };

                            if (!reader.IsDBNull(7))
                            {
                                item.ExpiresAt = DateTime.Parse(reader.GetString(7));
                            }

                            return item;
                        }
                    }
                }
            }

            return null;
        }

        public void DeleteItem(long id)
        {
            using (var connection = new SQLiteConnection(ConnectionString))
            {
                connection.Open();
                using (var command = new SQLiteCommand(connection))
                {
                    command.CommandText = "DELETE FROM ClipboardItems WHERE Id = @id";
                    command.Parameters.AddWithValue("@id", id);
                    command.ExecuteNonQuery();
                }
            }
        }

        public void DeleteAllItems()
        {
            using (var connection = new SQLiteConnection(ConnectionString))
            {
                connection.Open();
                using (var command = new SQLiteCommand(connection))
                {
                    command.CommandText = "DELETE FROM ClipboardItems";
                    command.ExecuteNonQuery();
                }
            }
        }

        public void SaveSetting(string key, string value)
        {
            using (var connection = new SQLiteConnection(ConnectionString))
            {
                connection.Open();
                using (var command = new SQLiteCommand(connection))
                {
                    command.CommandText = @"
                        INSERT OR REPLACE INTO Settings (Key, Value) VALUES (@key, @value)
                    ";
                    command.Parameters.AddWithValue("@key", key);
                    command.Parameters.AddWithValue("@value", value);
                    command.ExecuteNonQuery();
                }
            }
        }

        public string GetSetting(string key, string defaultValue = null)
        {
            using (var connection = new SQLiteConnection(ConnectionString))
            {
                connection.Open();
                using (var command = new SQLiteCommand(connection))
                {
                    command.CommandText = "SELECT Value FROM Settings WHERE Key = @key";
                    command.Parameters.AddWithValue("@key", key);

                    var result = command.ExecuteScalar();
                    return result != null ? result.ToString() : defaultValue;
                }
            }
        }

        // Method ini tetap ada untuk kompatibilitas
        public void SetItemRetention(long itemId, int hours)
        {
            using (var connection = new SQLiteConnection(ConnectionString))
            {
                connection.Open();
                using (var command = new SQLiteCommand(connection))
                {
                    command.CommandText = @"
                        UPDATE ClipboardItems 
                        SET ExpiresAt = datetime('now', '+' || @hours || ' hours')
                        WHERE Id = @id";
                    command.Parameters.AddWithValue("@hours", hours);
                    command.Parameters.AddWithValue("@id", itemId);
                    command.ExecuteNonQuery();
                }
            }
        }

        // OPTIONAL: Method tambahan untuk mendapatkan statistik
        public (int totalItems, int sensitiveItems, int expiredSoon) GetStatistics()
        {
            using (var connection = new SQLiteConnection(ConnectionString))
            {
                connection.Open();
                using (var command = new SQLiteCommand(connection))
                {
                    // Total items
                    command.CommandText = "SELECT COUNT(*) FROM ClipboardItems";
                    int totalItems = Convert.ToInt32(command.ExecuteScalar());

                    // Sensitive items
                    command.CommandText = "SELECT COUNT(*) FROM ClipboardItems WHERE IsSensitive = 1";
                    int sensitiveItems = Convert.ToInt32(command.ExecuteScalar());

                    // Items expiring in next hour
                    command.CommandText = @"
                        SELECT COUNT(*) FROM ClipboardItems 
                        WHERE ExpiresAt IS NOT NULL 
                        AND ExpiresAt < datetime('now', '+1 hour')";
                    int expiredSoon = Convert.ToInt32(command.ExecuteScalar());

                    return (totalItems, sensitiveItems, expiredSoon);
                }
            }
        }
    }
}