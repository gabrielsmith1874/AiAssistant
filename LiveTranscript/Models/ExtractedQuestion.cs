namespace LiveTranscript.Models
{
    public class ExtractedQuestion
    {
        public string Question { get; set; } = string.Empty;
        public bool IsFollowUp { get; set; }
        public string ParentQuestion { get; set; } = string.Empty;
    }
}
