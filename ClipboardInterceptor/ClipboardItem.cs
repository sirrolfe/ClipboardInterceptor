namespace ClipboardInterceptor
{
    public enum ClipboardItemType
    {
        Text,
        Image,
        File,
        Other
    }

    public class ClipboardItem
    {
        public long Id { get; set; }
        public DateTime Timestamp { get; set; }
        public ClipboardItemType ItemType { get; set; }
        public string EncryptedData { get; set; }
        public string ContentId { get; set; }
        public string Preview { get; set; }  // Encrypted short preview
        public bool IsSensitive { get; set; }
        public DateTime? ExpiresAt { get; set; }
    }
}