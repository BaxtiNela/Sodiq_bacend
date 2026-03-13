@echo off
echo Switching to Qwen2.5-Coder-7B...
set LLM_MODEL=qwen2.5-coder-7b-instruct-q4_k_m.gguf
cd /d "%~dp0"
docker compose build llama-server && docker compose up -d
echo.
echo Done! Qwen2.5-Coder-7B is now running.
pause
