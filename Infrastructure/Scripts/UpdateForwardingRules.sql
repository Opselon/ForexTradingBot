-- Update existing rules
UPDATE ForwardingRules
SET 
    SourceChannelId = -1001612196156,
    TargetChannelIds = '[-1001854636317]',
    EditOptions_RemoveLinks = 1,
    EditOptions_StripFormatting = 0,
    EditOptions_RemoveSourceForwardHeader = 0,
    EditOptions_DropAuthor = 0,
    EditOptions_DropMediaCaptions = 0,
    EditOptions_NoForwards = 0,
    FilterOptions_AllowedMessageTypes = '["Text","Photo","Video"]'
WHERE RuleName IN ('Channel1To2', 'Channel2To1');

-- Delete existing text replacements for these rules
DELETE FROM TextReplacementRule 
WHERE MessageEditOptionsForwardingRuleRuleName IN ('Channel1To2', 'Channel2To1');

-- Insert new text replacements
INSERT INTO TextReplacementRule (
    MessageEditOptionsForwardingRuleRuleName,
    Find,
    ReplaceWith,
    IsRegex,
    RegexOptions
)
VALUES 
('Channel1To2', 'https://wa.me/message/W6HXT7VWR3U2C1', '@Capxi', 0, 0),
('Channel2To1', 'https://wa.me/message/W6HXT7VWR3U2C1', '@Capxi', 0, 0);

-- Insert new rules if needed
IF NOT EXISTS (SELECT 1 FROM ForwardingRules WHERE RuleName = 'TestChannel1To2')
BEGIN
    INSERT INTO ForwardingRules (
        RuleName,
        IsEnabled,
        SourceChannelId,
        TargetChannelIds,
        EditOptions_RemoveLinks,
        EditOptions_StripFormatting,
        EditOptions_RemoveSourceForwardHeader,
        EditOptions_DropAuthor,
        EditOptions_DropMediaCaptions,
        EditOptions_NoForwards,
        EditOptions_PrependText,
        EditOptions_AppendText,
        EditOptions_CustomFooter,
        FilterOptions_AllowedMessageTypes
    )
    VALUES 
    (
        'TestChannel1To2',
        1,
        -1001612196156,
        '[-1001854636317]',
        1,
        0,
        0,
        0,
        0,
        0,
        '',
        '',
        '',
        '["Text","Photo","Video"]'
    );

    INSERT INTO TextReplacementRule (
        MessageEditOptionsForwardingRuleRuleName,
        Find,
        ReplaceWith,
        IsRegex,
        RegexOptions
    )
    VALUES ('TestChannel1To2', 'https://wa.me/message/W6HXT7VWR3U2C1', '@Capxi', 0, 0);
END

IF NOT EXISTS (SELECT 1 FROM ForwardingRules WHERE RuleName = 'TestChannel2To1')
BEGIN
    INSERT INTO ForwardingRules (
        RuleName,
        IsEnabled,
        SourceChannelId,
        TargetChannelIds,
        EditOptions_RemoveLinks,
        EditOptions_StripFormatting,
        EditOptions_RemoveSourceForwardHeader,
        EditOptions_DropAuthor,
        EditOptions_DropMediaCaptions,
        EditOptions_NoForwards,
        EditOptions_PrependText,
        EditOptions_AppendText,
        EditOptions_CustomFooter,
        FilterOptions_AllowedMessageTypes
    )
    VALUES 
    (
        'TestChannel2To1',
        1,
        -1001854636317,
        '[-1001612196156]',
        1,
        0,
        0,
        0,
        0,
        0,
        '',
        '',
        '',
        '["Text","Photo","Video"]'
    );

    INSERT INTO TextReplacementRule (
        MessageEditOptionsForwardingRuleRuleName,
        Find,
        ReplaceWith,
        IsRegex,
        RegexOptions
    )
    VALUES ('TestChannel2To1', 'https://wa.me/message/W6HXT7VWR3U2C1', '@Capxi', 0, 0);
END 