# ForexSignalBot 🤖📈

## Your Intelligent Assistant for Forex & Crypto Trading Signals and News!

[![.NET](https://img.shields.io/badge/.NET-9.0-blueviolet)](https://dotnet.microsoft.com/download/dotnet/9.0)
[![EF Core](https://img.shields.io/badge/EF%20Core-9.0-blue)](https://docs.microsoft.com/en-us/ef/core/)
[![Telegram.Bot](https://img.shields.io/badge/Telegram.Bot-19.0-blue.svg)](https://github.com/TelegramBots/Telegram.Bot)
[![Hangfire](https://img.shields.io/badge/Hangfire-Powered-orange)](https://www.hangfire.io/)
[![Docker](https://img.shields.io/badge/Docker-Ready-blue)](https://www.docker.com/)
[![Clean Architecture](https://img.shields.io/badge/Architecture-Clean%20%26%20DDD-lightgrey)](https://blog.cleancoder.com/uncle-bob/2012/08/13/the-clean-architecture.html)

---

**ForexSignalBot** is a comprehensive and powerful Telegram bot designed to provide users with timely Forex and cryptocurrency trading signals, market news, and advanced analytical insights. Built with a robust Clean Architecture and leveraging the latest .NET 9 technologies, this bot aims to be a reliable, scalable, and feature-rich platform for traders of all levels.

## ✨ Key Features

*   **Real-time Signal Delivery**: Get instant notifications for potential buy/sell signals in Forex and Crypto markets.
*   **Aggregated News Feeds**: Stay updated with the latest financial news from various configurable RSS sources.
*   **User-Specific Preferences**:
    *   **Category Subscription**: Users can subscribe/unsubscribe to specific news or signal categories (e.g., "Forex Majors", "Gold", "BTC Signals").
    *   **Notification Settings**: Fine-grained control over what notifications users receive (general, VIP, RSS news).
    *   **Language Preferences**: Multi-language support for a global user base (English by default, easily extendable).
*   **Subscription Tiers**:
    *   **Free Plan**: Access to basic features and public signals/news.
    *   **Premium Plans (VIP)**: Unlock exclusive VIP signals, advanced analytics, and priority notifications.
*   **Cryptocurrency Payments**: Secure and seamless subscription payments using **Crypto Pay API** (@CryptoBot / @CryptoTestnetBot).
*   **AI-Powered Insights (Future)**: Planned integration for sentiment analysis of news and potentially AI-generated signal insights.
*   **Interactive Telegram UI**:
    *   User-friendly command-based interaction (`/start`, `/menu`, `/settings`, `/signals`, `/subscribe`, `/profile`, `/help`).
    *   Dynamic Inline Keyboards for easy navigation and configuration.
    *   Professionally formatted messages with MarkdownV2, emojis, and clear information.
*   **Robust Background Processing**: Utilizes **Hangfire** for:
    *   Periodic fetching of RSS feeds.
    *   Asynchronous dispatch and sending of mass notifications to users, respecting Telegram API rate limits.
    *   Other background tasks like subscription management and data cleanup.
*   **User Management**: Secure registration and profile management.
*   **Admin Panel (via API - Future)**: Endpoints for managing users, RSS sources, signals, and system settings.

##  архитектура (Architecture)

This project strictly adheres to the principles of **Clean Architecture** and incorporates elements of **Domain-Driven Design (DDD)** to ensure maintainability, testability, and scalability.
