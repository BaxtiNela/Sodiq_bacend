@echo off
echo Switching to DeepSeek-Coder-6.7B...
set LLM_MODEL=deepseek-coder-6.7b-instruct-Q4_K_M.gguf
cd /d "%~dp0"
docker compose build llama-server && docker compose up -d
echo.
echo Done! DeepSeek-Coder-6.7B is now running.
pause
