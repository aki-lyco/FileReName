namespace Explore.Indexing
{
    public enum IndexPhase { Preparing, Scanning, Upserting, Completed, Canceled, Error }

    public sealed class IndexProgress
    {
        public IndexPhase Phase { get; init; }
        public long Processed { get; init; }
        public long Total { get; init; }
        public string? CurrentPath { get; init; }

        public double Percent =>
            Total <= 0 ? 0.0 : (double)Processed / Total * 100.0;
    }
}
