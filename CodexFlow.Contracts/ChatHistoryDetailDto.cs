using System;
using System.Collections.Generic;
using System.Text;

namespace CodexFlow.Contracts
{
    public class ChatHistoryDetailDto
    {
        public List<ChatMessageDto> Messages { get; set; } = new List<ChatMessageDto>();
        public List<ArchiveFileDto> ArchiveFiles { get; set; } = new List<ArchiveFileDto>();
    }
}
