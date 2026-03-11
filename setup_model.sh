#!/bin/bash
# Qwen2.5-7B-Instruct GGUF modelini yuklash (bir marta)
# Ishlatish: bash setup_model.sh

mkdir -p models

MODEL_FILE="models/qwen2.5-7b-q4_k_m.gguf"
MODEL_URL="https://huggingface.co/Qwen/Qwen2.5-7B-Instruct-GGUF/resolve/main/qwen2.5-7b-instruct-q4_k_m.gguf"

if [ -f "$MODEL_FILE" ]; then
    echo "Model allaqachon mavjud: $MODEL_FILE"
    exit 0
fi

echo "Qwen2.5-7B-Instruct Q4_K_M yuklanmoqda (~4.4 GB)..."
echo "URL: $MODEL_URL"
echo ""

if command -v wget &> /dev/null; then
    wget -c "$MODEL_URL" -O "$MODEL_FILE" --show-progress
elif command -v curl &> /dev/null; then
    curl -L -C - "$MODEL_URL" -o "$MODEL_FILE" --progress-bar
else
    echo "Xato: wget yoki curl o'rnatilmagan."
    exit 1
fi

echo ""
echo "Tayyor! Model saqlandi: $MODEL_FILE"
echo "Endi: docker compose up -d"
