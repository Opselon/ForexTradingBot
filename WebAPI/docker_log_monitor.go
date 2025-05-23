package main

import (
	"bufio"
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"strings"
	"time"
)

type TelegramMessage struct {
	ChatID    string `json:"chat_id"`
	Text      string `json:"text"`
	ParseMode string `json:"parse_mode"`
}

func sendToTelegram(message string) error {
	botToken := os.Getenv("TELEGRAM_BOT_TOKEN")
	channelID := os.Getenv("TELEGRAM_CHANNEL_ID")

	if botToken == "" || channelID == "" {
		return fmt.Errorf("missing environment variables: TELEGRAM_BOT_TOKEN or TELEGRAM_CHANNEL_ID")
	}

	apiURL := fmt.Sprintf("https://api.telegram.org/bot%s/sendMessage", botToken)
	
	telegramMsg := TelegramMessage{
		ChatID:    channelID,
		Text:      message,
		ParseMode: "HTML",
	}

	jsonData, err := json.Marshal(telegramMsg)
	if err != nil {
		return err
	}

	resp, err := http.Post(apiURL, "application/json", bytes.NewBuffer(jsonData))
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		body, _ := io.ReadAll(resp.Body)
		return fmt.Errorf("telegram API error: %s", string(body))
	}

	return nil
}

func formatLogMessage(containerName, logLine string) string {
	timestamp := time.Now().Format("2006-01-02 15:04:05")
	
	// Determine if it's an error or warning
	var emoji, level string
	if strings.Contains(strings.ToLower(logLine), "error") {
		emoji = "❌"
		level = "ERROR"
	} else {
		emoji = "⚠️"
		level = "WARNING"
	}

	return fmt.Sprintf("%s <b>%s</b>\n"+
		"🕒 %s\n"+
		"📦 Container: %s\n"+
		"\n%s",
		emoji, level, timestamp, containerName, logLine)
}

func monitorContainerLogs(containerName string) {
	cmd := exec.Command("docker", "logs", "-f", containerName)
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error creating stdout pipe: %v\n", err)
		return
	}

	if err := cmd.Start(); err != nil {
		fmt.Fprintf(os.Stderr, "Error starting docker logs: %v\n", err)
		return
	}

	scanner := bufio.NewScanner(stdout)
	for scanner.Scan() {
		line := scanner.Text()
		lowerLine := strings.ToLower(line)

		// Check if the line contains error or warning
		if strings.Contains(lowerLine, "error") || strings.Contains(lowerLine, "warning") {
			message := formatLogMessage(containerName, line)
			if err := sendToTelegram(message); err != nil {
				fmt.Fprintf(os.Stderr, "Error sending to Telegram: %v\n", err)
			}
		}
	}

	if err := scanner.Err(); err != nil {
		fmt.Fprintf(os.Stderr, "Error reading logs: %v\n", err)
	}

	cmd.Wait()
}

func main() {
	if len(os.Args) < 2 {
		fmt.Println("Usage: docker_log_monitor <container_name>")
		os.Exit(1)
	}

	containerName := os.Args[1]
	fmt.Printf("Monitoring logs for container: %s\n", containerName)
	monitorContainerLogs(containerName)
} 