import requests
import json
import os
from dotenv import load_dotenv

# Load environment variables from .env file
load_dotenv()

# Get credentials from environment variables
BOT_TOKEN = os.getenv('TELEGRAM_BOT_TOKEN')
CHANNEL_ID = os.getenv('TELEGRAM_CHANNEL_ID')

def test_bot():
    if not BOT_TOKEN or not CHANNEL_ID:
        print("Error: TELEGRAM_BOT_TOKEN and TELEGRAM_CHANNEL_ID must be set in .env file")
        return

    # Test getMe endpoint
    get_me_url = f"https://api.telegram.org/bot{BOT_TOKEN}/getMe"
    response = requests.get(get_me_url)
    print("Bot Info:", json.dumps(response.json(), indent=2))

    # Test sending message
    send_message_url = f"https://api.telegram.org/bot{BOT_TOKEN}/sendMessage"
    data = {
        "chat_id": CHANNEL_ID,
        "text": "🔍 Test message from ForexTradingBot\n\nThis is a test message to verify the bot and channel setup."
    }
    response = requests.post(send_message_url, json=data)
    print("\nSend Message Response:", json.dumps(response.json(), indent=2))

if __name__ == "__main__":
    test_bot() 