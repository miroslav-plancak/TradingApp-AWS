using Microsoft.EntityFrameworkCore;
using TradingApp.Domain.Models.Entities.DeadLetterLog;
using TradingApp.Domain.Models.Entities.Order;
using TradingApp.Domain.Models.Entities.OrderNotificationSequences;
using TradingApp.Domain.Models.Entities.OutboxMessage;
using TradingApp.Domain.Models.Entities.PendingFilledNotification;
using TradingApp.Domain.Models.Entities.QuarantinedOutboxMessage;
using TradingApp.Domain.Models.Entities.UnpublishedTopicMessages;
using TradingApp.Domain.Models.Enums;

namespace TradingApp.Domain
{
    public class TradingDbContext : DbContext
    {
        public TradingDbContext(DbContextOptions<TradingDbContext> options) : base(options) { }
        public DbSet<Order> Orders { get; set; }
        public DbSet<OutboxMessage> OutboxMessages {get; set; }
        public DbSet<DeadLetterLog> DeadLetterLogs { get; set;}
        public DbSet<QuarantinedOutboxMessage> QuarantinedOutboxMessages { get; set; }
        public DbSet<UnpublishedTopicMessage> UnpublishedTopicMessages { get; set; }
        public DbSet<OrderNotificationSequence> OrderNotificationSequences { get; set; }
        public DbSet<PendingFilledNotification> PendingFilledNotifications { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Order>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.Property(e => e.ClientOrderId).IsRequired();
                entity.HasIndex(e => e.ClientOrderId).IsUnique();

                entity.Property(e => e.Status).IsRequired();
                entity.Property(e => e.Quantity).IsRequired();
                entity.Property(e => e.Price).IsRequired().HasColumnType("decimal(18,2)");
                entity.Property(e => e.CreatedAt).IsRequired();
                entity.Property(e => e.UpdatedAt).IsRequired();
                entity.Property(e => e.IsProcessed).IsRequired();

                entity.Property(e => e.CorrelationId);
                entity.HasIndex(e => e.CorrelationId);
            });

            modelBuilder.Entity<OutboxMessage>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Type).IsRequired().HasMaxLength(200);
                entity.Property(e => e.Payload).IsRequired();
                entity.Property(e => e.CreatedAt).IsRequired();
                entity.Property(e => e.ProcessedAt);
                entity.Property(e => e.RetryCount).IsRequired();
                entity.Property(e => e.RetryReason).IsRequired().HasDefaultValue(OutboxRetryReason.None);
                entity.Property(e => e.LastError);

                entity.Property(e => e.CorrelationId);
                entity.HasIndex(e => e.CorrelationId);
            });

            modelBuilder.Entity<DeadLetterLog>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.Property(e => e.ClientOrderId).IsRequired();
                entity.HasIndex(e => e.ClientOrderId);

                entity.Property(e => e.MessageBody).IsRequired();
                entity.Property(e => e.Reason).IsRequired().HasMaxLength(500);
                entity.Property(e => e.CreatedAt).IsRequired();
                entity.Property(e => e.IsResolved).IsRequired();
                entity.Property(e => e.ResolutionNotes).HasMaxLength(2000);
                entity.Property(e => e.ResolvedAt);
                entity.Property(e => e.ResolvedBy).HasMaxLength(200);

                entity.Property(e => e.CorrelationId);
                entity.HasIndex(e => e.CorrelationId);
            });

            modelBuilder.Entity<QuarantinedOutboxMessage>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasIndex(e => e.OriginalOutboxMessageId);
                entity.Property(e => e.OriginalOutboxMessageId).IsRequired();

                entity.HasIndex(e => e.ClientOrderId);
                entity.Property(e => e.ClientOrderId);

                entity.Property(e => e.Payload).IsRequired();
                entity.Property(e => e.Reason).IsRequired();
                entity.Property(e => e.FinalRetryCount).IsRequired();
                entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
                entity.Property(e => e.QuarantinedAt).IsRequired();
                entity.Property(e => e.IsResurrected).IsRequired().HasDefaultValue(false);
                entity.Property(e => e.ResurrectedAt);
                entity.Property(e => e.IsDiscarded).IsRequired().HasDefaultValue(false);
                entity.Property(e => e.DiscardedAt);
                entity.Property(e => e.DiscardedBy).HasMaxLength(200);
                entity.Property(e => e.ResolutionNotes).HasMaxLength(2000);

                entity.Property(e => e.CorrelationId);
                entity.HasIndex(e => e.CorrelationId);
            });

            modelBuilder.Entity<UnpublishedTopicMessage>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasIndex(e => e.ClientOrderId);
                entity.Property(e => e.ClientOrderId);

                entity.Property(e => e.OrderStatus).IsRequired();
                entity.Property(e => e.ProcessedAt).IsRequired();
                entity.Property(e => e.CreatedAt).IsRequired();
                entity.Property(e => e.PublishedAt);
                entity.Property(e => e.RetryCount).IsRequired();
                entity.Property(e => e.LastError);

                entity.Property(e => e.CorrelationId);
                entity.HasIndex(e => e.CorrelationId);
            });

            modelBuilder.Entity<OrderNotificationSequence>(entity =>
            {
                entity.HasKey(e => e.ClientOrderId);
                entity.Property(e => e.ClientOrderId).ValueGeneratedNever();
                entity.Property(e => e.LastProcessedSequence).IsRequired();
                entity.Property(e => e.UpdatedAt).IsRequired();
            });

            modelBuilder.Entity<PendingFilledNotification>(entity =>
            {
                entity.HasKey(e => e.ClientOrderId);
                entity.Property(e => e.ClientOrderId).ValueGeneratedNever();

                entity.Property(e => e.EventPayload).IsRequired();
                entity.Property(e => e.CorrelationId).IsRequired().HasMaxLength(100);
                entity.Property(e => e.StoredAt).IsRequired();
            });
        }
    }
}
