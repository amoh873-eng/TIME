namespace time.Models
{
    /// <summary>يمثّل مهمة واحدة في تطبيق Time.</summary>
    public record TodoItem(long id, String title, String notes, bool done, String createdAt)
    {
    }
}