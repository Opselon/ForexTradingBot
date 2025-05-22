using Domain.Features.Forwarding.ValueObjects;
using System.Collections.Generic;

namespace Domain.Features.Forwarding.Entities
{
    public class ForwardingRule
    {
        public string RuleName { get; set; }
        public bool IsEnabled { get; set; }
        public long SourceChannelId { get; set; }
        public IReadOnlyList<long> TargetChannelIds { get; set; }
        public MessageEditOptions EditOptions { get; set; }
        public MessageFilterOptions FilterOptions { get; set; }

        public ForwardingRule() { } // For EF Core

        public ForwardingRule(
            string ruleName,
            bool isEnabled,
            long sourceChannelId,
            IReadOnlyList<long> targetChannelIds,
            MessageEditOptions editOptions,
            MessageFilterOptions filterOptions)
        {
            RuleName = ruleName;
            IsEnabled = isEnabled;
            SourceChannelId = sourceChannelId;
            TargetChannelIds = targetChannelIds;
            EditOptions = editOptions;
            FilterOptions = filterOptions;
        }

        public void UpdateStatus(bool isEnabled)
        {
            IsEnabled = isEnabled;
        }
    }
} 