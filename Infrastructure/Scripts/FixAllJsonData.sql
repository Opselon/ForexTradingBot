-- Fix TargetChannelIds (already correct, but included for completeness)
UPDATE ForwardingRules
SET TargetChannelIds = '[-1002696634930]'
WHERE RuleName = 'Channel1To2';

UPDATE ForwardingRules
SET TargetChannelIds = '[-1002504857154]'
WHERE RuleName = 'Channel2To1';

-- Fix AllowedMessageTypes
UPDATE ForwardingRules
SET FilterOptions_AllowedMessageTypes = '["Text","Photo","Video"]';

-- Fix AllowedMimeTypes
UPDATE ForwardingRules
SET FilterOptions_AllowedMimeTypes = '[]';

-- Fix AllowedSenderUserIds and BlockedSenderUserIds
UPDATE ForwardingRules
SET FilterOptions_AllowedSenderUserIds = '[]',
    FilterOptions_BlockedSenderUserIds = '[]';

-- Verify the changes
SELECT 
    RuleName,
    TargetChannelIds,
    FilterOptions_AllowedMessageTypes,
    FilterOptions_AllowedMimeTypes,
    FilterOptions_AllowedSenderUserIds,
    FilterOptions_BlockedSenderUserIds
FROM ForwardingRules; 