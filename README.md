# Text-to-Speech Generator

This Python application reads text from a `.txt` file and converts it to speech using Google Generative AI's built-in text-to-speech capabilities. The resulting audio is saved as a WAV file. Users can select from a variety of voices, and the filename always includes the selected voice name.

## Features

- **Direct Speech Synthesis**: Uses Google's Gemini AI model to convert text directly to natural-sounding speech.
- **Voice Selection**: Choose from 30 different voices with various characteristics (pitch, tone, etc.).
- **Random Voice Generation**: Generate multiple audio files with randomly selected voices for variety.
- **Flexible Output**: Saves audio to a specified file or automatically generates a filename in an `output` folder based on the content, voice, and timestamp.
- **Command-Line Interface**: Easy to use with command-line arguments.
- **Streaming Support**: Handles audio generation in chunks for efficient processing.

## Prerequisites

- Python 3.8 or higher
- Google Gemini API key

## Installation

1. **Clone or download the repository** to your local machine.

2. **Set up a Python virtual environment** (recommended):
   ```bash
   python -m venv .venv
   # On Windows:
   .venv\Scripts\activate
   # On macOS/Linux:
   source .venv/bin/activate
   ```

3. **Install dependencies**:
   ```bash
   pip install google-genai
   ```

## Authentication Setup

1. Go to [Google AI Studio](https://ai.google.dev/aistudio) and create an API key.
2. Set the environment variable:
   ```bash
   export GEMINI_API_KEY='your-gemini-api-key'
   ```
   On Windows PowerShell:
   ```powershell
   $env:GEMINI_API_KEY = 'your-gemini-api-key'
   ```

## Usage

1. **Create a text file**: Create a text file (e.g., `input.txt`) containing the text you want to convert to speech.

   Example `input.txt`:
   ```
   Hello, this is a test of the text-to-speech conversion.
   ```

2. **Run the application**:
   ```bash
   python text_to_speech.py input.txt
   ```
   This will convert the text to speech using the default voice (Autonoe) and save it to `output/Hello_this_is_Autonoe_20250913_143022.wav` (auto-generated filename).

   Or specify a custom output file and voice:
   ```bash
   python text_to_speech.py input.txt my_output.wav --voice Zephyr
   ```
   This saves to `Zephyr_my_output.wav`.

   Or generate multiple audio files with random voices:
   ```bash
   python text_to_speech.py input.txt --random-voices 3
   ```
   This generates 3 audio files with randomly selected voices, each with the voice name in the filename.

3. **Output**: The WAV file(s) will be saved in the specified location or in the `output` folder with auto-generated names that include the voice name.

## Command-Line Arguments

- `text_file`: Path to the input text file (required).
- `output_file`: Path to save the output WAV file (optional). If not provided, saves to `output/` folder with auto-generated name.
- `--voice`: The voice to use for speech synthesis (optional). Default: Autonoe. Available voices: Zephyr, Puck, Charon, Kore, Fenrir, Leda, Orus, Aoede, Callirrhoe, Autonoe, Enceladus, Iapetus, Umbriel, Algieba, Despina, Erinome, Algenib, Rasalgethi, Laomedeia, Achernar, Alnilam, Schedar, Gacrux, Pulcherrima, Achird, Zubenelgenubi, Vindemiatrix, Sadachbia, Sadaltager, Sulafat.
- `--random-voices`: Number of random voices to use (1-30). Generates multiple audio files with randomly selected voices (optional).

## How It Works

1. **Read Text**: The script reads the content of the specified text file.
2. **Generate Audio**: Uses Google Generative AI (Gemini 2.5 Flash Preview TTS model) to convert the text directly to speech audio using the selected voice.
3. **Stream and Save**: Collects the streamed audio data and saves it as a WAV file with the voice name included in the filename.

## Troubleshooting

- **Authentication Errors**: Ensure your GEMINI_API_KEY is correctly set.
- **Model Access**: The TTS model (`gemini-2.5-flash-preview-tts`) is in preview; ensure your API key has access.
- **Invalid Voice**: Choose from the list of available voices.
- **File Permissions**: Make sure the script has write permissions for the output directory.
- **Network Issues**: Ensure stable internet connection for API calls.

## Dependencies

- `google-genai`: For interacting with Google Generative AI and TTS.

## License

This project is for educational purposes. Please comply with Google's terms of service for API usage.
