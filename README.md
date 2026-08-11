# WASAPI-Audio-Ducker
WASAPI Audio Ducker (WAD) is a windows forms application that can be used to reduce the volume of target applications based upon the volume of trigger applications.

A modern, lightweight Windows Forms utility for Windows 10/11 that automatically lowers (ducks) the volume of target applications whenever specified trigger applications produce audio. Built using C# and the [NAudio](https://github.com/naudio/NAudio) library.

## Features

- **Real-Time Audio Monitoring:** Leverages WASAPI loopback and meter information to track active audio sessions instantly.
- **Customizable Controls:**
  - **Trigger Sensitivity:** Adjust how loud the trigger app needs to be before ducking kicks in.
  - **Ducked Volume:** Set the precise percentage to reduce the target app's volume.
  - **Release Hold Delay:** Configure how long the app waits after the trigger goes quiet before restoring target volumes.
- **Modern Windows 11 Design:** Clean dark theme UI featuring native Windows 11 rounded corners and system accent styling.
- **System Tray Support:** Minimizes seamlessly to the system tray to run quietly in the background with status updates.
- **Safety Safeguards:** Built-in mutual exclusion prevents an application from accidentally being selected as both a trigger and a target simultaneously.

## Requirements

- **Operating System:** Windows 10 or Windows 11
- **Runtime:** .NET runtime compatible with the project configuration (C# 9.0+)
- **Dependencies:** NAudio NuGet package

## Installation & Building

1. Clone the repository:
   ```bash
   git clone [https://github.com/DragonRage13/WASAPI-Audio-Ducker.git](https://github.com/DragonRage13/WASAPI-Audio-Ducker.git)
