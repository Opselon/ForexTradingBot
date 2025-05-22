-- First, let's see what we have in the database
SELECT RuleName, TargetChannelIds, FilterOptions_AllowedSenderUserIds, FilterOptions_BlockedSenderUserIds
FROM ForwardingRules;

-- Fix TargetChannelIds format
UPDATE ForwardingRules
SET TargetChannelIds = CASE 
    WHEN TargetChannelIds = '[]' THEN '[]'
    WHEN TargetChannelIds LIKE '[%]' THEN TargetChannelIds
    WHEN TargetChannelIds = '' THEN '[]'
    ELSE '[' + TargetChannelIds + ']'
END;

-- Fix AllowedSenderUserIds format
UPDATE ForwardingRules
SET FilterOptions_AllowedSenderUserIds = CASE 
    WHEN FilterOptions_AllowedSenderUserIds IS NULL THEN '[]'
    WHEN FilterOptions_AllowedSenderUserIds = '' THEN '[]'
    WHEN FilterOptions_AllowedSenderUserIds LIKE '[%]' THEN FilterOptions_AllowedSenderUserIds
    ELSE '[' + FilterOptions_AllowedSenderUserIds + ']'
END;

-- Fix BlockedSenderUserIds format
UPDATE ForwardingRules
SET FilterOptions_BlockedSenderUserIds = CASE 
    WHEN FilterOptions_BlockedSenderUserIds IS NULL THEN '[]'
    WHEN FilterOptions_BlockedSenderUserIds = '' THEN '[]'
    WHEN FilterOptions_BlockedSenderUserIds LIKE '[%]' THEN FilterOptions_BlockedSenderUserIds
    ELSE '[' + FilterOptions_BlockedSenderUserIds + ']'
END;

-- Let's see the fixed data
SELECT RuleName, TargetChannelIds, FilterOptions_AllowedSenderUserIds, FilterOptions_BlockedSenderUserIds
FROM ForwardingRules; 