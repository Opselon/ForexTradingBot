-- First, let's clean up any remaining nested arrays
UPDATE ForwardingRules
SET TargetChannelIds = REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(TargetChannelIds, '[[', '['), ']]', ']'), '[[', '['), ']]', ']'), '[[', '['), ']]', ']');

UPDATE ForwardingRules
SET FilterOptions_AllowedSenderUserIds = REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(FilterOptions_AllowedSenderUserIds, '[[', '['), ']]', ']'), '[[', '['), ']]', ']'), '[[', '['), ']]', ']');

UPDATE ForwardingRules
SET FilterOptions_BlockedSenderUserIds = REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(FilterOptions_BlockedSenderUserIds, '[[', '['), ']]', ']'), '[[', '['), ']]', ']'), '[[', '['), ']]', ']');

-- Now set the correct values
UPDATE ForwardingRules
SET TargetChannelIds = '[-1002696634930]'
WHERE RuleName = 'Channel1To2';

UPDATE ForwardingRules
SET TargetChannelIds = '[-1002504857154]'
WHERE RuleName = 'Channel2To1';

UPDATE ForwardingRules
SET FilterOptions_AllowedSenderUserIds = '[]',
    FilterOptions_BlockedSenderUserIds = '[]';

-- Show the results
SELECT 
    RuleName,
    'TargetChannelIds: ' + TargetChannelIds as TargetChannelIds,
    'AllowedSenderUserIds: ' + FilterOptions_AllowedSenderUserIds as AllowedSenderUserIds,
    'BlockedSenderUserIds: ' + FilterOptions_BlockedSenderUserIds as BlockedSenderUserIds
FROM ForwardingRules; 