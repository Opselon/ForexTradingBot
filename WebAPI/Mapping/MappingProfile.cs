using AutoMapper;
using Domain.Features.Forwarding.Entities;
using Domain.Features.Forwarding.ValueObjects;
using WebAPI.Models;

namespace WebAPI.Mapping
{
    public class MappingProfile : Profile
    {
        public MappingProfile()
        {
            // This mapping is now 100% accurate based on the provided MessageEditOptions.cs
            CreateMap<MessageEditOptionsDto, MessageEditOptions>()
                .ConstructUsing(src => new MessageEditOptions(
                    src.PrependText,
                    src.AppendText,
                    src.TextReplacements,
                    src.RemoveSourceForwardHeader,
                    src.RemoveLinks,
                    src.StripFormatting,
                    src.CustomFooter,
                    src.DropAuthor,
                    src.DropMediaCaptions,
                    src.NoForwards
                ));

            // This mapping is now 100% accurate based on the provided MessageFilterOptions.cs
            CreateMap<MessageFilterOptionsDto, MessageFilterOptions>()
                .ConstructUsing(src => new MessageFilterOptions(
                    src.AllowedMessageTypes,
                    src.AllowedMimeTypes,
                    src.ContainsText,
                    src.ContainsTextIsRegex,
                    src.ContainsTextRegexOptions,
                    src.AllowedSenderUserIds,
                    src.BlockedSenderUserIds,
                    src.IgnoreEditedMessages,
                    src.IgnoreServiceMessages,
                    src.MinMessageLength,
                    src.MaxMessageLength
                ));

            // This mapping correctly uses the above configurations to build the final entity.
            CreateMap<ForwardingRuleDto, ForwardingRule>()
                .ConstructUsing((src, context) => new ForwardingRule(
                    src.RuleName,
                    src.IsEnabled,
                    src.SourceChannelId,
                    src.TargetChannelIds,
                    context.Mapper.Map<MessageEditOptions>(src.EditOptions),
                    context.Mapper.Map<MessageFilterOptions>(src.FilterOptions)
                ));

            // NEW MAPPING: From the Domain Entity to the Summary DTO for GET requests
            CreateMap<ForwardingRule, ForwardingRuleSummaryDto>();
        }
    }
}