using Domain.Features.Forwarding.ValueObjects;
using System.Collections.Generic;

namespace Domain.Features.Forwarding.Entities
{
    public class ForwardingRule
    {
        public string RuleName { get; private set; }
        public bool IsEnabled { get; private set; }
        public long SourceChannelId { get; private set; }
        public IReadOnlyList<long> TargetChannelIds { get; private set; }
        public MessageEditOptions EditOptions { get; private set; }
        public MessageFilterOptions FilterOptions { get; private set; }

        private ForwardingRule() { } // For EF Core

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