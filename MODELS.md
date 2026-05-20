# Downloading GGUF Model Files

BrainApp requires GGUF model files placed in the `models/` folder. GGUF (Generic Graph Unified Format) is a file format for GGML-based models that can be loaded directly by LLamaSharp for fully offline inference.

## Default Models (Recommended)

### Chat Model (Qwen3-VL-8B recommended for most hardware)

| Model | Size | VRAM | Context | Description |
|-------|------|------|---------|-------------|
| Qwen/Qwen3-VL-8B-Instruct.Q3_K_M.gguf | ~4.5 GB | 6 GB | 8192 | Vision-language model, excellent for document understanding |
| Qwen/Qwen2.5-7B-Instruct-Q4_K_M.gguf | ~4.2 GB | 5 GB | 8192 | Strong general purpose, good Chinese/English |
| llama-3.2-3b-instruct-q4_k_m.gguf | ~1.8 GB | 3 GB | 8192 | Fast, efficient, good English responses |
| phi3.5-mini-instruct-q4_k_m.gguf | ~2.2 GB | 3 GB | 4096 | Microsoft's model, good reasoning |

### Embedding Model (for semantic search)

| Model | Size | Description |
|-------|------|-------------|
| nomic-embed-text-v1.5.Q4_K_M.gguf | ~274 MB | High-quality embeddings for semantic search |
| e5-mistral-7b-instruct-q4_k_m.gguf | ~4.5 GB | Higher quality but larger |

## Where to Download

### Hugging Face (Recommended)

1. Go to [huggingface.co](https://huggingface.co/models?other=gguf)
2. Search for the model name (e.g., "Qwen3-VL-8B-Instruct-GGUF")
3. Look for the "Files" tab with `.gguf` files
4. Download the quantized version (Q3_K_M or Q4_K_M recommended)

Example URLs:
- Qwen3-VL: https://huggingface.co/Qwen/Qwen3-VL-8B-Instruct-GGUF
- Llama3.2: https://huggingface.co/unsloth/llama-3.2-3b-instruct-GGUF
- Phi3.5: https://huggingface.co/microsoft/Phi-3.5-mini-instruct-GGUF

### The Bloke (Alternative)

For more quantized options: https://huggingface.co/TheBloke

## Recommended Quantizations

| Quantization | Quality | Speed | Size | Recommendation |
|--------------|---------|-------|------|----------------|
| Q2_K | Lower | Fastest | Smallest | Only if VRAM < 4GB |
| Q3_K_M | Good | Fast | Medium | Good balance |
| **Q4_K_M** | **Very Good** | **Fast** | **Medium** | **Recommended** |
| Q5_K_M | Excellent | Medium | Larger | Best quality |
| Q8_0 | Near perfect | Slower | Large | If you have plenty of VRAM |

## GPU Memory Requirements

The actual VRAM needed depends on:
- Model size
- Quantization level
- Context size
- Number of GPU layers

Rough guide for Q4_K_M at context 8192:
- 3B model: ~3 GB VRAM
- 7B model: ~5 GB VRAM
- 8B model: ~6 GB VRAM
- 13B model: ~10 GB VRAM

For CPU-only (GpuLayerCount=0), you need:
- ~2x the model size in RAM
- Q4_K_M recommended for 8GB+ RAM systems

## Setting Up Models

1. Place `.gguf` files in the `models/` folder next to your `appsettings.json`
2. Update `appsettings.json` with the correct file names:

```json
{
  "LLama": {
    "ModelsFolder": "models",
    "ChatModelFile": "Qwen.Qwen3-VL-8B-Instruct.Q3_K_M.gguf",
    "EmbeddingModelFile": "nomic-embed-text-v1.5.Q4_K_M.gguf"
  }
}
```

3. Restart BrainApp — the loading screen will show progress

## Chat Templates

BrainApp supports multiple chat templates. Match to your model:

| Template | Models |
|----------|--------|
| Qwen | Qwen series |
| Llama3 | Llama 3, Llama 3.1, Llama 3.2 |
| Phi3 | Phi-3 series |
| Gemma | Gemma series |
| Mistral | Mistral, Mixtral |
| ChatML | Chat models using ChatML format |

## Validation

To verify model files are correctly placed:

1. Open BrainApp Settings → About tab
2. Click "Check model files"
3. Both chat and embedding models should show "Found" with file sizes

## Troubleshooting

**Model not found error:**
- Check file name matches exactly (case-sensitive)
- Verify file is in the `models/` folder
- Check `ModelsFolder` path in appsettings.json

**Out of memory:**
- Reduce GpuLayerCount to 0 for CPU inference
- Lower context size (e.g., 4096)
- Use a smaller quantization (Q3_K_M instead of Q4_K_M)
- Use a smaller model (3B instead of 8B)

**Slow inference:**
- Enable GPU layers (set GpuLayerCount > 0)
- Increase threads (set Threads to CPU core count)
- Use Q4_K_M quantization for balance of speed/quality