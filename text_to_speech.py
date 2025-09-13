import base64
import mimetypes
import os
import re
import struct
import random
from google import genai
from google.genai import types
from datetime import datetime

# List of available voices
VOICES = [
    "Zephyr", "Puck", "Charon", "Kore", "Fenrir", "Leda", "Orus", "Aoede",
    "Callirrhoe", "Autonoe", "Enceladus", "Iapetus", "Umbriel", "Algieba",
    "Despina", "Erinome", "Algenib", "Rasalgethi", "Laomedeia", "Achernar",
    "Alnilam", "Schedar", "Gacrux", "Pulcherrima", "Achird", "Zubenelgenubi",
    "Vindemiatrix", "Sadachbia", "Sadaltager", "Sulafat"
]

def save_binary_file(file_name, data):
    with open(file_name, "wb") as f:
        f.write(data)
    print(f"File saved to: {file_name}")

def convert_to_wav(audio_data: bytes, mime_type: str) -> bytes:
    """Generates a WAV file header for the given audio data and parameters.

    Args:
        audio_data: The raw audio data as a bytes object.
        mime_type: Mime type of the audio data.

    Returns:
        A bytes object representing the WAV file header.
    """
    parameters = parse_audio_mime_type(mime_type)
    bits_per_sample = parameters["bits_per_sample"]
    sample_rate = parameters["rate"]
    num_channels = 1
    data_size = len(audio_data)
    bytes_per_sample = bits_per_sample // 8
    block_align = num_channels * bytes_per_sample
    byte_rate = sample_rate * block_align
    chunk_size = 36 + data_size  # 36 bytes for header fields before data chunk size

    # http://soundfile.sapp.org/doc/WaveFormat/

    header = struct.pack(
        "<4sI4s4sIHHIIHH4sI",
        b"RIFF",          # ChunkID
        chunk_size,       # ChunkSize (total file size - 8 bytes)
        b"WAVE",          # Format
        b"fmt ",          # Subchunk1ID
        16,               # Subchunk1Size (16 for PCM)
        1,                # AudioFormat (1 for PCM)
        num_channels,     # NumChannels
        sample_rate,      # SampleRate
        byte_rate,        # ByteRate
        block_align,      # BlockAlign
        bits_per_sample,  # BitsPerSample
        b"data",          # Subchunk2ID
        data_size         # Subchunk2Size (size of audio data)
    )
    return header + audio_data

def parse_audio_mime_type(mime_type: str) -> dict[str, int | None]:
    """Parses bits per sample and rate from an audio MIME type string.

    Assumes bits per sample is encoded like "L16" and rate as "rate=xxxxx".

    Args:
        mime_type: The audio MIME type string (e.g., "audio/L16;rate=24000").

    Returns:
        A dictionary with "bits_per_sample" and "rate" keys. Values will be
        integers if found, otherwise None.
    """
    bits_per_sample = 16
    rate = 24000

    # Extract rate from parameters
    parts = mime_type.split(";")
    for param in parts:  # Skip the main type part
        param = param.strip()
        if param.lower().startswith("rate="):
            try:
                rate_str = param.split("=", 1)[1]
                rate = int(rate_str)
            except (ValueError, IndexError):
                # Handle cases like "rate=" with no value or non-integer value
                pass  # Keep rate as default
        elif param.startswith("audio/L"):
            try:
                bits_per_sample = int(param.split("L", 1)[1])
            except (ValueError, IndexError):
                pass  # Keep bits_per_sample as default if conversion fails

    return {"bits_per_sample": bits_per_sample, "rate": rate}

def text_to_speech(text_file, output_file=None, voice="Autonoe"):
    """
    Converts text from a file to speech using Google Generative AI's TTS model and saves it as an audio file.
    The filename always includes the selected voice name.

    Args:
        text_file (str): The path to the input text file.
        output_file (str, optional): The path to save the output audio file. Defaults to None.
        voice (str): The voice to use for speech synthesis.
    """
    try:
        print("Starting text-to-speech conversion...")
        client = genai.Client(api_key=os.getenv("GEMINI_API_KEY"))
        print("Gemini client initialized.")

        # Read the text from the file
        with open(text_file, 'r') as f:
            text = f.read()
        print(f"Read {len(text)} characters from {text_file}")

        print(f"Selected voice: {voice}")

        # Determine output file path
        if output_file is None:
            os.makedirs('output', exist_ok=True)
            title = '_'.join(text.split()[:3]) if text.split() else 'generated'
            # Sanitize title for filename
            title = ''.join(c for c in title if c.isalnum() or c in (' ', '-', '_')).rstrip()
            timestamp = datetime.now().strftime('%Y%m%d_%H%M%S')
            output_file = f'output/{title}_{voice}_{timestamp}.wav'
        else:
            # Always include voice in the filename
            base, ext = os.path.splitext(output_file)
            output_file = f'{voice}_{base}{ext}'
        print(f"Output will be saved to: {output_file}")

        model = "gemini-2.5-flash-preview-tts"
        contents = [
            types.Content(
                role="user",
                parts=[types.Part.from_text(text=text)],
            ),
        ]
        generate_content_config = types.GenerateContentConfig(
            temperature=1,
            response_modalities=["audio"],
            speech_config=types.SpeechConfig(
                voice_config=types.VoiceConfig(
                    prebuilt_voice_config=types.PrebuiltVoiceConfig(
                        voice_name=voice
                    )
                ),
            ),
        )

        print("Starting audio generation...")
        audio_data = b""
        chunk_count = 0
        for chunk in client.models.generate_content_stream(
            model=model,
            contents=contents,
            config=generate_content_config,
        ):
            if (
                chunk.candidates is None
                or chunk.candidates[0].content is None
                or chunk.candidates[0].content.parts is None
            ):
                continue
            if chunk.candidates[0].content.parts[0].inline_data and chunk.candidates[0].content.parts[0].inline_data.data:
                inline_data = chunk.candidates[0].content.parts[0].inline_data
                audio_data += inline_data.data
                chunk_count += 1
                print(f"Processed chunk {chunk_count}, total audio data: {len(audio_data)} bytes")

        if audio_data:
            print(f"Audio generation complete. Total data: {len(audio_data)} bytes")
            inline_data = chunk.candidates[0].content.parts[0].inline_data  # Use the last chunk's mime_type
            file_extension = mimetypes.guess_extension(inline_data.mime_type)
            if file_extension is None:
                print("Converting audio to WAV format...")
                file_extension = ".wav"
                audio_data = convert_to_wav(audio_data, inline_data.mime_type)
            else:
                output_file = output_file.replace('.wav', file_extension)  # Adjust extension if needed
            print("Saving audio file...")
            save_binary_file(output_file, audio_data)
        else:
            print("No audio data generated.")

    except Exception as e:
        print(f"An error occurred: {e}")
        print("Please ensure you have set GEMINI_API_KEY for Generative AI.")
        print("See https://ai.google.dev/aistudio for Generative AI API key.")

if __name__ == '__main__':
    import argparse

    parser = argparse.ArgumentParser(description='Convert text from a file to speech using Google AI.')
    parser.add_argument('text_file', help='The path to the input text file.')
    parser.add_argument('output_file', nargs='?', default=None, help='The path to save the output audio file (optional).')
    parser.add_argument('--voice', choices=VOICES, default='Autonoe', help='The voice to use for speech synthesis (default: Autonoe).')
    parser.add_argument('--random-voices', type=int, default=None, help='Number of random voices to use (1-30). Generates multiple audio files with random voices.')
    args = parser.parse_args()

    if args.random_voices:
        if args.random_voices < 1 or args.random_voices > len(VOICES):
            print(f"Number of random voices must be between 1 and {len(VOICES)}")
            exit(1)
        selected_voices = random.sample(VOICES, args.random_voices)
        print(f"Selected random voices: {', '.join(selected_voices)}")
        for voice in selected_voices:
            output_file = args.output_file
            if output_file:
                base, ext = os.path.splitext(output_file)
                output_file = f'{voice}_{base}{ext}'
            text_to_speech(args.text_file, output_file, voice)
    else:
        text_to_speech(args.text_file, args.output_file, args.voice)
