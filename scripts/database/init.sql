-- ============================================
-- Database Initialization Script for ForexTradingBot
-- ============================================
-- This script runs automatically when PostgreSQL container starts

-- Create database if it doesn't exist
CREATE DATABASE forexbotdb;

\c forexbotdb

-- Enable required extensions
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- Create basic users table (if not managed by EF Core)
CREATE TABLE IF NOT EXISTS Users (
    Id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    TelegramUserId BIGINT NOT NULL UNIQUE,
    Username VARCHAR(255),
    FullName VARCHAR(255),
    CreatedAt TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    IsActive BOOLEAN DEFAULT TRUE,
    SubscriptionTier VARCHAR(50) DEFAULT 'free',
    LastActivity TIMESTAMP WITH TIME ZONE
);

-- Create trading signals table
CREATE TABLE IF NOT EXISTS TradingSignals (
    Id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    Symbol VARCHAR(20) NOT NULL,
    Type VARCHAR(10) NOT NULL CHECK (Type IN ('BUY', 'SELL')),
    EntryPrice DECIMAL(20, 8) NOT NULL,
    StopLoss DECIMAL(20, 8),
    TakeProfit1 DECIMAL(20, 8),
    TakeProfit2 DECIMAL(20, 8),
    TakeProfit3 DECIMAL(20, 8),
    Leverage DECIMAL(5, 2),
    Status VARCHAR(20) DEFAULT 'pending' CHECK (Status IN ('pending', 'active', 'closed', 'cancelled')),
    ClosedAt TIMESTAMP WITH TIME ZONE,
    ProfitLoss DECIMAL(20, 8),
    SignalProvider VARCHAR(100),
    CreatedAt TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

-- Create indexes for performance
CREATE INDEX IF NOT EXISTS idx_trading_signals_symbol ON TradingSignals(Symbol);
CREATE INDEX IF NOT EXISTS idx_trading_signals_status ON TradingSignals(Status);
CREATE INDEX IF NOT EXISTS idx_users_telegram_user_id ON Users(TelegramUserId);
CREATE INDEX IF NOT EXISTS idx_users_created_at ON Users(CreatedAt);

-- Grant permissions
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO postgres;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO postgres;

COMMENT ON TABLE Users IS 'Telegram users subscribed to the bot';
COMMENT ON TABLE TradingSignals IS 'Generated trading signals from AI analysis';
