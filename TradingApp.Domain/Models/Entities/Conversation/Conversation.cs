using System;

namespace TradingApp.Domain.Models.Entities.Conversation
{
    public class Conversation
    {
        public Guid Id { get; set; }
        public required string Name { get; set; }
        public required DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
    }
}
