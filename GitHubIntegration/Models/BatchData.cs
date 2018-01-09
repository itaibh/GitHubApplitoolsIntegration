using System.Collections.Generic;

namespace GitHubIntegration.Models
{
    public class BatchData
    {
        public int RunningCount { get; set; }
        public int AbortedCount { get; set; }
        public int DifferentCount { get; set; }
        public int NewCount { get; set; }
        public int DiffResolvedCount { get; set; }
        public int CompletedCount { get; set; }
        public int PassedCount { get; set; }
        public int FailedCount { get; set; }
        public int UnresolvedCount { get; set; }
        public int ChangedCount { get; set; }
        public int StarredCount { get; set; }
        public int NewMismatchCount { get; set; }
        public int NewRemarkCount { get; set; }

        public string Id { get; set; }
        public string Name { get; set; }

        public int Version { get; set; }
        public int Revision { get; set; }

        public string RowKey { get; set; }
        public bool IsCandidate { get; set; }

        public override string ToString()
        {
            List<string> retVals = new List<string>();

            if (RunningCount > 0)
            {
                retVals.Add(RunningCount + " running");
            }

            if (PassedCount > 0)
            {
                retVals.Add(PassedCount + " passed");
            }

            if (FailedCount > 0)
            {
                retVals.Add(FailedCount + " failed");
            }

            if (UnresolvedCount > 0)
            {
                retVals.Add(UnresolvedCount + " unresolved");
            }

            return string.Join(", ", retVals);
        }
    }
}