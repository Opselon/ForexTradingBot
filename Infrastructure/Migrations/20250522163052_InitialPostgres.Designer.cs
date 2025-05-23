﻿// <auto-generated />
using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20250522163052_InitialPostgres")]
    partial class InitialPostgres
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "9.0.5")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("Domain.Entities.NewsItem", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<string>("AffectedAssets")
                        .HasMaxLength(500)
                        .HasColumnType("character varying(500)");

                    b.Property<Guid?>("AssociatedSignalCategoryId")
                        .HasColumnType("uuid");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("DetectedLanguage")
                        .HasMaxLength(10)
                        .HasColumnType("character varying(10)");

                    b.Property<string>("FullContent")
                        .HasColumnType("text");

                    b.Property<string>("ImageUrl")
                        .HasMaxLength(2083)
                        .HasColumnType("character varying(2083)");

                    b.Property<bool>("IsVipOnly")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("boolean")
                        .HasDefaultValue(false);

                    b.Property<DateTime?>("LastProcessedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Link")
                        .IsRequired()
                        .HasMaxLength(2083)
                        .HasColumnType("character varying(2083)");

                    b.Property<DateTime>("PublishedDate")
                        .HasColumnType("timestamp with time zone");

                    b.Property<Guid>("RssSourceId")
                        .HasColumnType("uuid");

                    b.Property<string>("SentimentLabel")
                        .HasMaxLength(50)
                        .HasColumnType("character varying(50)");

                    b.Property<double?>("SentimentScore")
                        .HasColumnType("double precision");

                    b.Property<string>("SourceItemId")
                        .HasMaxLength(500)
                        .HasColumnType("character varying(500)");

                    b.Property<string>("SourceName")
                        .HasMaxLength(150)
                        .HasColumnType("character varying(150)");

                    b.Property<string>("Summary")
                        .HasColumnType("text");

                    b.Property<string>("Title")
                        .IsRequired()
                        .HasMaxLength(500)
                        .HasColumnType("character varying(500)");

                    b.HasKey("Id");

                    b.HasIndex("AssociatedSignalCategoryId");

                    b.HasIndex("Link");

                    b.HasIndex("RssSourceId", "SourceItemId")
                        .IsUnique()
                        .HasFilter("\"SourceItemId\" IS NOT NULL");

                    b.ToTable("NewsItems", (string)null);
                });

            modelBuilder.Entity("Domain.Entities.RssSource", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<DateTime>("CreatedAt")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("timestamp with time zone")
                        .HasDefaultValueSql("NOW()");

                    b.Property<Guid?>("DefaultSignalCategoryId")
                        .HasColumnType("uuid");

                    b.Property<string>("Description")
                        .HasMaxLength(1000)
                        .HasColumnType("character varying(1000)");

                    b.Property<string>("ETag")
                        .HasMaxLength(255)
                        .HasColumnType("character varying(255)");

                    b.Property<int>("FetchErrorCount")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasDefaultValue(0);

                    b.Property<int?>("FetchIntervalMinutes")
                        .HasColumnType("integer");

                    b.Property<bool>("IsActive")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("boolean")
                        .HasDefaultValue(true);

                    b.Property<DateTime?>("LastFetchAttemptAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("LastModifiedHeader")
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)");

                    b.Property<DateTime?>("LastSuccessfulFetchAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("SourceName")
                        .IsRequired()
                        .HasMaxLength(150)
                        .HasColumnType("character varying(150)");

                    b.Property<DateTime?>("UpdatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Url")
                        .IsRequired()
                        .HasMaxLength(2083)
                        .HasColumnType("character varying(2083)");

                    b.HasKey("Id");

                    b.HasIndex("DefaultSignalCategoryId");

                    b.HasIndex("SourceName");

                    b.HasIndex("Url")
                        .IsUnique();

                    b.ToTable("RssSources", (string)null);
                });

            modelBuilder.Entity("Domain.Entities.Signal", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<Guid>("CategoryId")
                        .HasColumnType("uuid");

                    b.Property<DateTime?>("ClosedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<decimal>("EntryPrice")
                        .HasColumnType("decimal(18, 8)");

                    b.Property<bool>("IsVipOnly")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("boolean")
                        .HasDefaultValue(false);

                    b.Property<string>("Notes")
                        .HasMaxLength(1000)
                        .HasColumnType("character varying(1000)");

                    b.Property<DateTime>("PublishedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("SourceProvider")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)");

                    b.Property<string>("Status")
                        .IsRequired()
                        .ValueGeneratedOnAdd()
                        .HasColumnType("text")
                        .HasDefaultValue("Pending");

                    b.Property<decimal>("StopLoss")
                        .HasColumnType("decimal(18, 8)");

                    b.Property<string>("Symbol")
                        .IsRequired()
                        .HasMaxLength(50)
                        .HasColumnType("character varying(50)");

                    b.Property<decimal>("TakeProfit")
                        .HasColumnType("decimal(18, 8)");

                    b.Property<string>("Timeframe")
                        .HasMaxLength(10)
                        .HasColumnType("character varying(10)");

                    b.Property<string>("Type")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<DateTime?>("UpdatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.HasKey("Id");

                    b.HasIndex("CategoryId");

                    b.HasIndex("Symbol");

                    b.ToTable("Signals", (string)null);
                });

            modelBuilder.Entity("Domain.Entities.SignalAnalysis", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<string>("AnalysisText")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<string>("AnalystName")
                        .IsRequired()
                        .HasMaxLength(150)
                        .HasColumnType("character varying(150)");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<double?>("SentimentScore")
                        .HasColumnType("double precision");

                    b.Property<Guid>("SignalId")
                        .HasColumnType("uuid");

                    b.HasKey("Id");

                    b.HasIndex("SignalId");

                    b.ToTable("SignalAnalyses", (string)null);
                });

            modelBuilder.Entity("Domain.Entities.SignalCategory", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<string>("Description")
                        .HasMaxLength(500)
                        .HasColumnType("character varying(500)");

                    b.Property<bool>("IsActive")
                        .HasColumnType("boolean");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)");

                    b.Property<int>("SortOrder")
                        .HasColumnType("integer");

                    b.HasKey("Id");

                    b.HasIndex("Name")
                        .IsUnique();

                    b.ToTable("SignalCategories", (string)null);
                });

            modelBuilder.Entity("Domain.Entities.Subscription", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<Guid?>("ActivatingTransactionId")
                        .HasColumnType("uuid");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<DateTime>("EndDate")
                        .HasColumnType("timestamp with time zone");

                    b.Property<DateTime>("StartDate")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Status")
                        .IsRequired()
                        .HasMaxLength(50)
                        .HasColumnType("character varying(50)");

                    b.Property<DateTime?>("UpdatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<Guid>("UserId")
                        .HasColumnType("uuid");

                    b.HasKey("Id");

                    b.HasIndex("EndDate");

                    b.HasIndex("UserId");

                    b.ToTable("Subscriptions", (string)null);
                });

            modelBuilder.Entity("Domain.Entities.TokenWallet", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<decimal>("Balance")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("decimal(18, 8)")
                        .HasDefaultValue(0m);

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<bool>("IsActive")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("boolean")
                        .HasDefaultValue(true);

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<Guid>("UserId")
                        .HasColumnType("uuid");

                    b.HasKey("Id");

                    b.HasIndex("UserId")
                        .IsUnique();

                    b.ToTable("TokenWallets", (string)null);
                });

            modelBuilder.Entity("Domain.Entities.Transaction", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<decimal>("Amount")
                        .HasColumnType("decimal(18, 4)");

                    b.Property<string>("Description")
                        .HasMaxLength(500)
                        .HasColumnType("character varying(500)");

                    b.Property<DateTime?>("PaidAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("PaymentGatewayInvoiceId")
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)");

                    b.Property<string>("PaymentGatewayName")
                        .HasMaxLength(50)
                        .HasColumnType("character varying(50)");

                    b.Property<string>("PaymentGatewayPayload")
                        .HasColumnType("text");

                    b.Property<string>("PaymentGatewayResponse")
                        .HasColumnType("text");

                    b.Property<string>("Status")
                        .IsRequired()
                        .ValueGeneratedOnAdd()
                        .HasMaxLength(50)
                        .HasColumnType("character varying(50)")
                        .HasDefaultValue("Pending");

                    b.Property<DateTime>("Timestamp")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Type")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<Guid>("UserId")
                        .HasColumnType("uuid");

                    b.HasKey("Id");

                    b.HasIndex("PaymentGatewayInvoiceId");

                    b.HasIndex("Status");

                    b.HasIndex("Timestamp");

                    b.HasIndex("UserId");

                    b.ToTable("Transactions", (string)null);
                });

            modelBuilder.Entity("Domain.Entities.User", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Email")
                        .IsRequired()
                        .HasMaxLength(200)
                        .HasColumnType("character varying(200)");

                    b.Property<bool>("EnableGeneralNotifications")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("boolean")
                        .HasDefaultValue(true);

                    b.Property<bool>("EnableRssNewsNotifications")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("boolean")
                        .HasDefaultValue(true);

                    b.Property<bool>("EnableVipSignalNotifications")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("boolean")
                        .HasDefaultValue(false);

                    b.Property<string>("Level")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<string>("PreferredLanguage")
                        .IsRequired()
                        .ValueGeneratedOnAdd()
                        .HasMaxLength(10)
                        .HasColumnType("character varying(10)")
                        .HasDefaultValue("en");

                    b.Property<string>("TelegramId")
                        .IsRequired()
                        .HasMaxLength(50)
                        .HasColumnType("character varying(50)");

                    b.Property<DateTime?>("UpdatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Username")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)");

                    b.HasKey("Id");

                    b.HasIndex("Email")
                        .IsUnique();

                    b.HasIndex("TelegramId")
                        .IsUnique();

                    b.HasIndex("Username")
                        .IsUnique();

                    b.ToTable("Users", (string)null);
                });

            modelBuilder.Entity("Domain.Entities.UserSignalPreference", b =>
                {
                    b.Property<Guid>("UserId")
                        .HasColumnType("uuid");

                    b.Property<Guid>("CategoryId")
                        .HasColumnType("uuid");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<Guid>("Id")
                        .HasColumnType("uuid");

                    b.HasKey("UserId", "CategoryId");

                    b.HasIndex("CategoryId");

                    b.ToTable("UserSignalPreferences", (string)null);
                });

            modelBuilder.Entity("Domain.Features.Forwarding.Entities.ForwardingRule", b =>
                {
                    b.Property<string>("RuleName")
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)");

                    b.Property<bool>("IsEnabled")
                        .HasColumnType("boolean");

                    b.Property<long>("SourceChannelId")
                        .HasColumnType("bigint");

                    b.Property<string>("TargetChannelIds")
                        .IsRequired()
                        .HasColumnType("text");

                    b.HasKey("RuleName");

                    b.ToTable("ForwardingRules", (string)null);
                });

            modelBuilder.Entity("Domain.Entities.NewsItem", b =>
                {
                    b.HasOne("Domain.Entities.SignalCategory", "AssociatedSignalCategory")
                        .WithMany()
                        .HasForeignKey("AssociatedSignalCategoryId")
                        .OnDelete(DeleteBehavior.SetNull);

                    b.HasOne("Domain.Entities.RssSource", "RssSource")
                        .WithMany("NewsItems")
                        .HasForeignKey("RssSourceId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("AssociatedSignalCategory");

                    b.Navigation("RssSource");
                });

            modelBuilder.Entity("Domain.Entities.RssSource", b =>
                {
                    b.HasOne("Domain.Entities.SignalCategory", "DefaultSignalCategory")
                        .WithMany()
                        .HasForeignKey("DefaultSignalCategoryId")
                        .OnDelete(DeleteBehavior.SetNull);

                    b.Navigation("DefaultSignalCategory");
                });

            modelBuilder.Entity("Domain.Entities.Signal", b =>
                {
                    b.HasOne("Domain.Entities.SignalCategory", "Category")
                        .WithMany("Signals")
                        .HasForeignKey("CategoryId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired();

                    b.Navigation("Category");
                });

            modelBuilder.Entity("Domain.Entities.SignalAnalysis", b =>
                {
                    b.HasOne("Domain.Entities.Signal", "Signal")
                        .WithMany("Analyses")
                        .HasForeignKey("SignalId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Signal");
                });

            modelBuilder.Entity("Domain.Entities.Subscription", b =>
                {
                    b.HasOne("Domain.Entities.User", "User")
                        .WithMany("Subscriptions")
                        .HasForeignKey("UserId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("User");
                });

            modelBuilder.Entity("Domain.Entities.TokenWallet", b =>
                {
                    b.HasOne("Domain.Entities.User", "User")
                        .WithOne("TokenWallet")
                        .HasForeignKey("Domain.Entities.TokenWallet", "UserId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("User");
                });

            modelBuilder.Entity("Domain.Entities.Transaction", b =>
                {
                    b.HasOne("Domain.Entities.User", "User")
                        .WithMany("Transactions")
                        .HasForeignKey("UserId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("User");
                });

            modelBuilder.Entity("Domain.Entities.UserSignalPreference", b =>
                {
                    b.HasOne("Domain.Entities.SignalCategory", "Category")
                        .WithMany("UserPreferences")
                        .HasForeignKey("CategoryId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("Domain.Entities.User", "User")
                        .WithMany("Preferences")
                        .HasForeignKey("UserId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Category");

                    b.Navigation("User");
                });

            modelBuilder.Entity("Domain.Features.Forwarding.Entities.ForwardingRule", b =>
                {
                    b.OwnsOne("Domain.Features.Forwarding.ValueObjects.MessageEditOptions", "EditOptions", b1 =>
                        {
                            b1.Property<string>("ForwardingRuleRuleName")
                                .HasColumnType("character varying(100)");

                            b1.Property<string>("AppendText")
                                .HasColumnType("text");

                            b1.Property<string>("CustomFooter")
                                .HasColumnType("text");

                            b1.Property<bool>("DropAuthor")
                                .HasColumnType("boolean");

                            b1.Property<bool>("DropMediaCaptions")
                                .HasColumnType("boolean");

                            b1.Property<bool>("NoForwards")
                                .HasColumnType("boolean");

                            b1.Property<string>("PrependText")
                                .HasColumnType("text");

                            b1.Property<bool>("RemoveLinks")
                                .HasColumnType("boolean");

                            b1.Property<bool>("RemoveSourceForwardHeader")
                                .HasColumnType("boolean");

                            b1.Property<bool>("StripFormatting")
                                .HasColumnType("boolean");

                            b1.HasKey("ForwardingRuleRuleName");

                            b1.ToTable("ForwardingRules");

                            b1.WithOwner()
                                .HasForeignKey("ForwardingRuleRuleName");

                            b1.OwnsMany("Domain.Features.Forwarding.ValueObjects.TextReplacementRule", "TextReplacements", b2 =>
                                {
                                    b2.Property<string>("MessageEditOptionsForwardingRuleRuleName")
                                        .HasColumnType("character varying(100)");

                                    b2.Property<int>("Id")
                                        .ValueGeneratedOnAdd()
                                        .HasColumnType("integer");

                                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b2.Property<int>("Id"));

                                    b2.Property<string>("Find")
                                        .IsRequired()
                                        .HasColumnType("text");

                                    b2.Property<bool>("IsRegex")
                                        .HasColumnType("boolean");

                                    b2.Property<int>("RegexOptions")
                                        .HasColumnType("integer");

                                    b2.Property<string>("ReplaceWith")
                                        .IsRequired()
                                        .HasColumnType("text");

                                    b2.HasKey("MessageEditOptionsForwardingRuleRuleName", "Id");

                                    b2.ToTable("TextReplacementRule");

                                    b2.WithOwner()
                                        .HasForeignKey("MessageEditOptionsForwardingRuleRuleName");
                                });

                            b1.Navigation("TextReplacements");
                        });

                    b.OwnsOne("Domain.Features.Forwarding.ValueObjects.MessageFilterOptions", "FilterOptions", b1 =>
                        {
                            b1.Property<string>("ForwardingRuleRuleName")
                                .HasColumnType("character varying(100)");

                            b1.Property<string>("AllowedMessageTypes")
                                .HasColumnType("text");

                            b1.Property<string>("AllowedMimeTypes")
                                .HasColumnType("text");

                            b1.Property<string>("AllowedSenderUserIds")
                                .HasColumnType("text");

                            b1.Property<string>("BlockedSenderUserIds")
                                .HasColumnType("text");

                            b1.Property<string>("ContainsText")
                                .HasColumnType("text");

                            b1.Property<bool>("ContainsTextIsRegex")
                                .HasColumnType("boolean");

                            b1.Property<int>("ContainsTextRegexOptions")
                                .HasColumnType("integer");

                            b1.Property<bool>("IgnoreEditedMessages")
                                .HasColumnType("boolean");

                            b1.Property<bool>("IgnoreServiceMessages")
                                .HasColumnType("boolean");

                            b1.Property<int?>("MaxMessageLength")
                                .HasColumnType("integer");

                            b1.Property<int?>("MinMessageLength")
                                .HasColumnType("integer");

                            b1.HasKey("ForwardingRuleRuleName");

                            b1.ToTable("ForwardingRules");

                            b1.WithOwner()
                                .HasForeignKey("ForwardingRuleRuleName");
                        });

                    b.Navigation("EditOptions")
                        .IsRequired();

                    b.Navigation("FilterOptions")
                        .IsRequired();
                });

            modelBuilder.Entity("Domain.Entities.RssSource", b =>
                {
                    b.Navigation("NewsItems");
                });

            modelBuilder.Entity("Domain.Entities.Signal", b =>
                {
                    b.Navigation("Analyses");
                });

            modelBuilder.Entity("Domain.Entities.SignalCategory", b =>
                {
                    b.Navigation("Signals");

                    b.Navigation("UserPreferences");
                });

            modelBuilder.Entity("Domain.Entities.User", b =>
                {
                    b.Navigation("Preferences");

                    b.Navigation("Subscriptions");

                    b.Navigation("TokenWallet")
                        .IsRequired();

                    b.Navigation("Transactions");
                });
#pragma warning restore 612, 618
        }
    }
}
