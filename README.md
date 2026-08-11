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
- **System Tray Support:** Minimizes seamlessly to the system tray to run quietly in the background with status update.
-  **Minimal System Strain:** Optimzed to use as little resources as possible.
- **Safety Safeguards:** Built-in mutual exclusion prevents an application from accidentally being selected as both a trigger and a target simultaneously.

<div align="center">

  <h3>Download the latest version</h3>

  <a href="https://github.com/DragonRage13/WASAPI-Audio-Ducker/releases/latest">
    <img src="https://img.shields.io/badge/📥_Download_Latest_Release-blue?style=for-the-badge&logo=github" alt="Download Latest Release" height="45">
  </a>

</div>

<table>
  <tr>
    <td>
      <p><strong>In-Active</strong></p>
    </td>
    <td>
      <p><strong>Active</strong></p>
    </td>
  </tr>
  <tr>
    <td width="50%">
      <img src="https://github.com/user-attachments/assets/cbe01a10-71ae-4b54-9100-b47ddb457f9e" alt="Screenshot 1" />
    </td>
    <td width="50%">
      <img src="https://github.com/user-attachments/assets/a37cbdac-b5f5-407a-b2bd-9ff2ecc97e1b" alt="Screenshot 2" />
    </td>
  </tr>
  <tr>
    <td colspan="2">
      <p>Sits comfortably in the background</p>
    </td>
  </tr>
  <tr>
    <td colspan="2">
      <img width="100" height="46" alt="image" src="https://github.com/user-attachments/assets/3eb3bd7c-ab4b-41f7-ad97-5925853d9142" />
    </td>
  </tr>
</table>




## Requirements

- **Operating System:** Windows 10 or Windows 11
- **Runtime:** .NET runtime compatible with the project configuration (C# 9.0+)
- **Dependencies:** NAudio NuGet package

## Installation & Building

1. Clone the repository:
   ```bash
   git clone [https://github.com/DragonRage13/WASAPI-Audio-Ducker.git](https://github.com/DragonRage13/WASAPI-Audio-Ducker.git)
