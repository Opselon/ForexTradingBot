-- ============================================
-- Database Initialization Script for ForexTradingBot
-- ============================================
-- Runs once, automatically, when the PostgreSQL container starts
-- (docker-entrypoint-initdb.d). Database "forexbotdb" is already created
-- by POSTGRES_DB, so no CREATE DATABASE here (it would abort init with
-- "database already exists").
--
-- NOTE: application tables are created by EF Core at first startup
-- (Infrastructure/Data/DatabaseProviderConfigurator.EnsureSchemaAsync),
-- so this script must NOT define application tables — a hand-written
-- schema would be out of sync with the model.

-- Extensions required by the application model
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS pgcrypto;

COMMENT ON DATABASE forexbotdb IS 'ForexTradingBot application database (schema created by EF Core)';
