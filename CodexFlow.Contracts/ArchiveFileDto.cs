using System;
using System.Collections.Generic;
using System.Text;

namespace CodexFlow.Contracts
{
    public class ArchiveFileDto
    {
        public required string Id { get; set; }
        public required string FileName { get; set; }
        public required string FileContent { get; set; }
        public DateTime? CreateAt { get; set; }
    }
}
