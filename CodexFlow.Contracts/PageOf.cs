namespace CodexFlow.Contracts
{
    public class PageOf<T> where T : class
    {
        public required List<T> List { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int Total { get; set; }
    }
}
