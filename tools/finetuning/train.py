#!/usr/bin/env python3
"""
VoiceChatAI - Fine-tuning script using Unsloth.

Trains a LoRA adapter on corporate meeting data (transcripts, feedback)
and exports it for use with Ollama.

Usage:
    python train.py --data ./collected_feedback.json --output ./lora_adapter

Requirements:
    pip install -r requirements.txt
"""

import argparse
import json
import os
import sys
from typing import List, Dict

try:
    import torch
    from unsloth import FastLanguageModel, is_bfloat16_supported
    from datasets import Dataset
    from transformers import TrainingArguments
    from trl import SFTTrainer
except ImportError:
    print("Error: Unsloth not installed. Run: pip install -r requirements.txt")
    sys.exit(1)


def parse_args():
    parser = argparse.ArgumentParser(description="Fine-tune Llama 3 with LoRA for VoiceChatAI")
    parser.add_argument("--data", type=str, default="./collected_feedback.json",
                        help="Path to training data JSON file")
    parser.add_argument("--output", type=str, default="./lora_adapter",
                        help="Output directory for LoRA adapter")
    parser.add_argument("--model", type=str, default="unsloth/llama-3-8b-Instruct-bnb-4bit",
                        help="Base model name")
    parser.add_argument("--max-seq-length", type=int, default=2048,
                        help="Maximum sequence length")
    parser.add_argument("--batch-size", type=int, default=2,
                        help="Per-device batch size")
    parser.add_argument("--gradient-accumulation-steps", type=int, default=4,
                        help="Gradient accumulation steps")
    parser.add_argument("--learning-rate", type=float, default=2e-4,
                        help="Learning rate")
    parser.add_argument("--num-epochs", type=int, default=3,
                        help="Number of training epochs")
    parser.add_argument("--lora-rank", type=int, default=16,
                        help="LoRA rank")
    parser.add_argument("--lora-alpha", type=int, default=32,
                        help="LoRA alpha")
    return parser.parse_args()


def load_training_data(data_path: str) -> List[Dict[str, str]]:
    """Load and validate training data from JSON file."""
    if not os.path.exists(data_path):
        print(f"Error: Data file not found: {data_path}")
        print("Creating sample data file for testing...")
        sample_data = [
            {
                "instruction": "Analyze the meeting transcript and provide a summary of key topics.",
                "input": "We discussed the Q4 budget, marketing campaign for the new product launch, and resource allocation for the engineering team.",
                "output": "Key topics discussed: 1) Q4 budget planning, 2) Marketing campaign for new product launch, 3) Engineering resource allocation."
            },
            {
                "instruction": "Detect if the meeting topic has changed based on recent transcripts.",
                "input": "Previous topic: budget planning. New transcript: Let's talk about the marketing strategy for next quarter.",
                "output": "Topic change detected. New topic: Marketing strategy for next quarter."
            },
            {
                "instruction": "Suggest improvements for the current meeting.",
                "input": "One participant has been speaking for 3 minutes without interruption.",
                "output": "Tip: Consider inviting other participants to share their thoughts on this topic to ensure diverse perspectives."
            },
            {
                "instruction": "Suggest alternative ideas for the current discussion topic.",
                "input": "The team is discussing using traditional advertising channels for the product launch.",
                "output": "Alternative idea: Have you considered digital marketing channels like social media campaigns or influencer partnerships? They often provide better ROI for product launches."
            }
        ]
        with open(data_path, "w", encoding="utf-8") as f:
            json.dump(sample_data, f, ensure_ascii=False, indent=2)
        print(f"Sample data written to {data_path}")
        return sample_data

    with open(data_path, "r", encoding="utf-8") as f:
        data = json.load(f)

    if not isinstance(data, list):
        raise ValueError("Training data must be a JSON array")

    for i, item in enumerate(data):
        if "instruction" not in item or "output" not in item:
            raise ValueError(f"Item {i} missing required fields 'instruction' and 'output'")

    print(f"Loaded {len(data)} training examples from {data_path}")
    return data


def format_prompts(data: List[Dict[str, str]]) -> List[str]:
    """Format training data into Alpaca-style prompts."""
    texts = []
    for item in data:
        instruction = item["instruction"]
        input_text = item.get("input", "")
        output = item["output"]

        if input_text:
            prompt = f"""### Instruction:
{instruction}

### Input:
{input_text}

### Response:
{output}"""
        else:
            prompt = f"""### Instruction:
{instruction}

### Response:
{output}"""
        texts.append(prompt)
    return texts


def main():
    args = parse_args()

    print("=" * 60)
    print("VoiceChatAI - Unsloth Fine-tuning")
    print("=" * 60)
    print(f"Base model: {args.model}")
    print(f"LoRA rank: {args.lora_rank}, alpha: {args.lora_alpha}")
    print(f"Output: {args.output}")
    print(f"Data: {args.data}")
    print("=" * 60)

    # Load and prepare data
    raw_data = load_training_data(args.data)
    texts = format_prompts(raw_data)
    dataset = Dataset.from_dict({"text": texts})

    print(f"Dataset size: {len(dataset)} examples")

    # Load model with 4-bit quantization
    print("Loading model...")
    model, tokenizer = FastLanguageModel.from_pretrained(
        model_name=args.model,
        max_seq_length=args.max_seq_length,
        dtype=None,
        load_in_4bit=True,
    )

    # Add LoRA adapters
    print("Adding LoRA adapters...")
    model = FastLanguageModel.get_peft_model(
        model,
        r=args.lora_rank,
        target_modules=[
            "q_proj", "k_proj", "v_proj", "o_proj",
            "gate_proj", "up_proj", "down_proj",
        ],
        lora_alpha=args.lora_alpha,
        lora_dropout=0,
        bias="none",
        use_gradient_checkpointing="unsloth",
        random_state=42,
        use_rslora=False,
        loftq_config=None,
    )

    # Training arguments
    training_args = TrainingArguments(
        per_device_train_batch_size=args.batch_size,
        gradient_accumulation_steps=args.gradient_accumulation_steps,
        warmup_steps=5,
        num_train_epochs=args.num_epochs,
        learning_rate=args.learning_rate,
        fp16=not is_bfloat16_supported(),
        bf16=is_bfloat16_supported(),
        logging_steps=1,
        optim="adamw_8bit",
        weight_decay=0.01,
        lr_scheduler_type="linear",
        seed=42,
        output_dir=args.output,
        report_to="none",
    )

    # Trainer
    trainer = SFTTrainer(
        model=model,
        tokenizer=tokenizer,
        train_dataset=dataset,
        dataset_text_field="text",
        max_seq_length=args.max_seq_length,
        dataset_num_proc=2,
        packing=False,
        args=training_args,
    )

    # Train
    print("Starting training...")
    trainer.train()

    # Save adapter
    print(f"Saving LoRA adapter to {args.output}...")
    model.save_pretrained(args.output)
    tokenizer.save_pretrained(args.output)

    # Save in GGUF/ollama compatible format
    print("Saving in Ollama-compatible format...")
    model.save_pretrained_gguf(
        os.path.join(args.output, "gguf"),
        tokenizer,
        quantization_method="q4_k_m",
    )

    print("=" * 60)
    print("Training complete!")
    print(f"LoRA adapter saved to: {args.output}")
    print(f"GGUF model saved to: {os.path.join(args.output, 'gguf')}")
    print()
    print("To use with Ollama:")
    print(f"  1. Copy the adapter to Ollama's models directory")
    print(f"  2. Restart Ollama: docker compose restart ollama")
    print("=" * 60)


if __name__ == "__main__":
    main()
