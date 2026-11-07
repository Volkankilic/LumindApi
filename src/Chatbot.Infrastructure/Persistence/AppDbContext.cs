using System.Text;
using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Infrastructure.Persistence;

/// <summary>
/// EF Core is only the ORM here. The schema is created by hand from api/db/*.sql; the application
/// never runs DDL (no Migrate, no EnsureCreated). Keep this model and api/db/ in step: the drift test checks it.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Participant> Participants => Set<Participant>();

    public DbSet<ConsentRecord> ConsentRecords => Set<ConsentRecord>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();

    public DbSet<AdminRefreshToken> AdminRefreshTokens => Set<AdminRefreshToken>();

    public DbSet<AdminRecoveryCode> AdminRecoveryCodes => Set<AdminRecoveryCode>();

    public DbSet<CrisisAcknowledgement> CrisisAcknowledgements => Set<CrisisAcknowledgement>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("public");

        b.Entity<Participant>(e =>
        {
            e.ToTable("participants", t => t.HasCheckConstraint("ck_participants_status", EnumCheck("status", typeof(ParticipantStatus))));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Status).HasConversion<string>();
            e.HasIndex(x => x.CodeHash).IsUnique().HasDatabaseName("ux_participants_code_hash");
            e.HasIndex(x => x.Label).HasDatabaseName("ix_participants_label");
            // 003_admin.sql: a label names one participant (empty labels are not compared).
            e.HasIndex(x => x.Label, "ux_participants_label_unique").IsUnique().HasFilter("label IS NOT NULL AND label <> ''").HasDatabaseName("ux_participants_label");
        });

        b.Entity<ConsentRecord>(e =>
        {
            e.ToTable("consent_records", t =>
            {
                t.HasCheckConstraint("ck_consent_records_type", EnumCheck("type", typeof(ConsentType)));
                // "Notice read" is an event, never a consent that can be refused.
                t.HasCheckConstraint("ck_consent_records_notice_granted", "type <> 'PrivacyNotice' OR granted");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Type).HasConversion<string>();
            e.HasOne<Participant>().WithMany().HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ParticipantId, x.Type, x.Timestamp }).IsDescending(false, false, true)
                .HasDatabaseName("ix_consent_records_participant_type_time");
        });

        b.Entity<Conversation>(e =>
        {
            e.ToTable("conversations", t => t.HasCheckConstraint("ck_conversations_peplau_phase", EnumCheck("peplau_phase", typeof(PeplauPhase))));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Ignore(x => x.IsActive);
            e.Property(x => x.PeplauPhase).HasConversion<string>().HasDefaultValue(PeplauPhase.Orientation);
            e.HasOne<Participant>().WithMany().HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Restrict);
            // One active conversation per participant, enforced by the database (also under races).
            e.HasIndex(x => x.ParticipantId).IsUnique().HasFilter("ended_at IS NULL")
                .HasDatabaseName("ux_conversations_one_active_per_participant");
            e.HasIndex(x => new { x.ParticipantId, x.StartedAt }).IsDescending(false, true)
                .HasDatabaseName("ix_conversations_participant_started");
        });

        b.Entity<Message>(e =>
        {
            e.ToTable("messages", t =>
            {
                t.HasCheckConstraint("ck_messages_role", EnumCheck("role", typeof(MessageRole)));
                t.HasCheckConstraint("ck_messages_completion", EnumCheck("completion", typeof(MessageCompletion), nullable: true));
                t.HasCheckConstraint("ck_messages_completion_assistant_only", "completion IS NULL OR role = 'Assistant'");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Role).HasConversion<string>();
            e.Property(x => x.Completion).HasConversion<string>();
            e.Property(x => x.IsCrisis).HasDefaultValue(false);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
            // Idempotency key: the same client message id can exist once per conversation.
            e.HasIndex(x => new { x.ConversationId, x.ClientMessageId }).IsUnique()
                .HasFilter("client_message_id IS NOT NULL").HasDatabaseName("ux_messages_conversation_client_message");
            e.HasIndex(x => new { x.ConversationId, x.CreatedAt }).HasDatabaseName("ix_messages_conversation_created");
            // 003_admin.sql: the admin crisis list reads the flagged messages by time.
            e.HasIndex(x => x.CreatedAt, "ix_messages_crisis_created_model").HasFilter("is_crisis").HasDatabaseName("ix_messages_crisis_created");
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne<Participant>().WithMany().HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_refresh_tokens_token_hash");
            e.HasIndex(x => x.FamilyId).HasDatabaseName("ix_refresh_tokens_family");
            e.HasIndex(x => x.ParticipantId).HasDatabaseName("ix_refresh_tokens_participant");
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs", t => t.HasCheckConstraint("ck_audit_logs_actor_type", EnumCheck("actor_type", typeof(ActorType))));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ActorType).HasConversion<string>();
            // No foreign key on actor_id: audit entries must outlive a deleted participant.
            e.HasIndex(x => new { x.ActorId, x.Timestamp }).HasDatabaseName("ix_audit_logs_actor_time");
            // 003_admin.sql: login counts and first/last login per participant.
            e.HasIndex(x => new { x.ActorId, x.Action, x.Timestamp }).HasDatabaseName("ix_audit_logs_actor_action_time");
            // 002_retention_indexes.sql: the retention job deletes audit rows by age.
            e.HasIndex(x => x.Timestamp).HasDatabaseName("ix_audit_logs_timestamp");
            // A second index on the same column needs a model name of its own, otherwise EF folds it into the one above.
            e.HasIndex(x => x.Timestamp, "ix_audit_logs_anonymous_timestamp").HasFilter("actor_type = 'Anonymous'").HasDatabaseName("ix_audit_logs_anonymous_timestamp");
        });

        b.Entity<AdminUser>(e =>
        {
            e.ToTable("admin_users", t => t.HasCheckConstraint("ck_admin_users_status", EnumCheck("status", typeof(AdminStatus))));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.TotpEnabled).HasDefaultValue(false);
            e.Property(x => x.TotpLastStep).HasDefaultValue(0L);
            e.Property(x => x.FailedAttempts).HasDefaultValue(0);
            e.HasIndex(x => x.Username).IsUnique().HasDatabaseName("ux_admin_users_username");
        });

        b.Entity<AdminRefreshToken>(e =>
        {
            e.ToTable("admin_refresh_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne<AdminUser>().WithMany().HasForeignKey(x => x.AdminId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_admin_refresh_tokens_token_hash");
            e.HasIndex(x => x.FamilyId).HasDatabaseName("ix_admin_refresh_tokens_family");
            e.HasIndex(x => x.AdminId).HasDatabaseName("ix_admin_refresh_tokens_admin");
        });

        b.Entity<AdminRecoveryCode>(e =>
        {
            e.ToTable("admin_recovery_codes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne<AdminUser>().WithMany().HasForeignKey(x => x.AdminId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AdminId, x.CodeHash }).IsUnique().HasDatabaseName("ux_admin_recovery_codes_admin_hash");
        });

        b.Entity<CrisisAcknowledgement>(e =>
        {
            e.ToTable("crisis_acknowledgements");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            // The flagged message goes away with its conversation (retention, erasure): the acknowledgement goes with it.
            e.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AdminUser>().WithMany().HasForeignKey(x => x.AdminId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.MessageId).IsUnique().HasFilter("reopened_at IS NULL").HasDatabaseName("ux_crisis_acknowledgements_open_message");
            e.HasIndex(x => x.MessageId, "ix_crisis_acknowledgements_message_model").HasDatabaseName("ix_crisis_acknowledgements_message");
            // 004_crisis_ack_admin_index.sql: the admin_id foreign key has an index (EF expects one for every foreign key).
            e.HasIndex(x => x.AdminId).HasDatabaseName("ix_crisis_acknowledgements_admin");
        });

        ApplySnakeCase(b);
    }

    private static string EnumCheck(string column, Type enumType, bool nullable = false)
    {
        var values = string.Join(", ", Enum.GetNames(enumType).Select(n => $"'{n}'"));
        return nullable ? $"{column} IS NULL OR {column} IN ({values})" : $"{column} IN ({values})";
    }

    /// <summary>Columns, keys and foreign keys are snake_case in the database (no extra naming package needed).</summary>
    private static void ApplySnakeCase(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var p in entity.GetProperties())
            {
                p.SetColumnName(ToSnake(p.Name));
            }

            entity.FindPrimaryKey()!.SetName($"pk_{entity.GetTableName()}");

            foreach (var fk in entity.GetForeignKeys())
            {
                fk.SetConstraintName($"fk_{entity.GetTableName()}_{ToSnake(fk.Properties[0].Name)}");
            }
        }
    }

    internal static string ToSnake(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
