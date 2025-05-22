-- Update TargetChannelIds to JSON format if not already
UPDATE ForwardingRules
SET TargetChannelIds = CASE 
    WHEN TargetChannelIds = '[]' THEN '[]'
    WHEN TargetChannelIds LIKE '[%]' THEN TargetChannelIds
    ELSE '[' + REPLACE(TargetChannelIds, ',', ',') + ']'
END;

-- Update AllowedMessageTypes to JSON format
UPDATE ForwardingRules
SET FilterOptions_AllowedMessageTypes = CASE 
    WHEN FilterOptions_AllowedMessageTypes IS NULL THEN '[]'
    WHEN FilterOptions_AllowedMessageTypes = '' THEN '[]'
    WHEN FilterOptions_AllowedMessageTypes LIKE '[%]' THEN FilterOptions_AllowedMessageTypes
    ELSE '["' + REPLACE(FilterOptions_AllowedMessageTypes, ',', '","') + '"]'
END;

-- Update AllowedMimeTypes to JSON format
UPDATE ForwardingRules
SET FilterOptions_AllowedMimeTypes = CASE 
    WHEN FilterOptions_AllowedMimeTypes IS NULL THEN '[]'
    WHEN FilterOptions_AllowedMimeTypes = '' THEN '[]'
    WHEN FilterOptions_AllowedMimeTypes LIKE '[%]' THEN FilterOptions_AllowedMimeTypes
    ELSE '["' + REPLACE(FilterOptions_AllowedMimeTypes, ',', '","') + '"]'
END;

-- Update AllowedSenderUserIds to JSON format
UPDATE ForwardingRules
SET FilterOptions_AllowedSenderUserIds = CASE 
    WHEN FilterOptions_AllowedSenderUserIds IS NULL THEN '[]'
    WHEN FilterOptions_AllowedSenderUserIds = '' THEN '[]'
    WHEN FilterOptions_AllowedSenderUserIds LIKE '[%]' THEN FilterOptions_AllowedSenderUserIds
    ELSE '[' + REPLACE(FilterOptions_AllowedSenderUserIds, ',', ',') + ']'
END;

-- Update BlockedSenderUserIds to JSON format
UPDATE ForwardingRules
SET FilterOptions_BlockedSenderUserIds = CASE 
    WHEN FilterOptions_BlockedSenderUserIds IS NULL THEN '[]'
    WHEN FilterOptions_BlockedSenderUserIds = '' THEN '[]'
    WHEN FilterOptions_BlockedSenderUserIds LIKE '[%]' THEN FilterOptions_BlockedSenderUserIds
    ELSE '[' + REPLACE(FilterOptions_BlockedSenderUserIds, ',', ',') + ']'
END; 