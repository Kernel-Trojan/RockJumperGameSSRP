# RockJumper

RockJumper is a VR game project developed in Unity. This repository contains the source code, assets, and documentation for the game. This game is developed as a part of Signal, Systems and Random Processes Course (ES 244) at IIT Gandhinagar.

## Hardware Integration
This project uses an Arduino and a Force Sensitive Resistor (FSR) to measure jump force and calculate jump height. The Arduino reads the FSR data and communicates with the Unity game to provide real-time jump input.

## Signal Processing
The `Signal Processing` folder contains various filters applied to the FSR data. We use these filters to process 5 minutes of raw FSR data, improving accuracy and reliability of jump detection and height calculation.

## Features
- Immersive VR gameplay
- Physics-based jumping mechanics
- Customizable player controls

## Getting Started

### Prerequisites
- Unity 6.6
- Arduino IDE
- Arduino board
- FSR (Force Sensitive Resistor) sensor
- Python 3.14
- Compatible VR headset (e.g., Oculus, HTC Vive, Oculus, or any PC-tethered VR headset using OpenXR)

### Setup
1. Clone this repository:
   ```sh
   git clone https://github.com/Kernel-Trojan/RockJumperGameSSRP.git
   ```
2. Create a Python virtual environment and install dependencies:
   ```sh
   python -m venv venv
   venv\Scripts\activate  # On Windows
   pip install -r requirements.txt
   ```
3. Upload the provided `fsr_live.ino` Arduino code to your Arduino board and connect the FSR sensor.
4. Run any of the filter scripts in the `Signal Processing` folder. When running the Python script, wait 3 seconds to capture the rest signal.
5. Open Unity Hub, click 'Add', then 'Add project from disk', and select the `FSRJumpVR` directory.
6. Let Unity compile the project, then launch the game.
7. In-game:
   - Press the `K` key to calibrate the highest jump force, then press the FSR sensor.
   - Hold the right mouse button to move around and use the FSR to jump.
   - If the FSR is not connected, you can use the spacebar to play.

## Project Structure
- `Assets/` - Game assets and scripts
- `ProjectSettings/` - Unity project settings
- `README.md` - Project documentation
- `Signal Processing` - 5 minute signal sample per member

## Contribution
- Satyam Suman
- Archit Sinha
- Akshat Mathews
- Ayushman Arya

## License
This project is licensed under the MIT License. See `LICENSE` for details.
