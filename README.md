# GameTask for Xbox Game Bar

**GameTask** is a custom Xbox Game Bar extension designed to bring seamless task management and utility controls directly into your gaming overlay. 

Access your essential tasks without ever alt-tabbing or leaving your game.

---

## ⚙️ In a nutshell

1. **Launch and Kill apps:** The core feature is to be able to Launch and Kill apps with a single press of the Action Buttons.
2. **Focus and Fullscreen :** Bring certain apps to front, and send Custom Keys or Maximise command to make them occupy the whole screen.
3. **Multipath setup:** Some apps launch with an .exe, but switch window later.  Multipath allows targeting different services for the "same" app.
4. **Customisation options :**  Use custom icons, Emojis, Rename apps, and move them around.
5. **Customisation options s :** Categorise your apps and games however you want, assing each acategoy custom colors and behaviors.
   
---

---

## 🎮 Overview

GameTask integrates directly into the Windows ecosystem. 

By pressing `Win + G`, users can pull up the GameTask widget to manage their workflow, trigger actions, or monitor processes. 

GameTask utilizes a dual-architecture design: an overlay UI and a background desktop launcher.

## 🏗️ Project Architecture

This repository contains two primary components:

* **Core Widget (`WidgetTemplate` project):** The front-end user interface. This is the AppX/UWP component that renders inside the Xbox Game Bar overlay. *(Note: The internal project folder retains the 'WidgetTemplate' naming convention, but serves as the main UI).*
* **GameTask Launcher (`Launcher` project):** A companion Win32/desktop application. This helper process bridges the gap between the Game Bar sandbox and the host OS, executing background tasks and system-level operations on behalf of the widget.

---

## ⚙️ How It Works

1. **Initialization:** When the user opens the GameTask widget via the Game Bar, the core UI initializes.
2. **Execution:** For system-level tasks, the widget communicates with the **Launcher** component via App Execution Aliases or local app services. Make sure this is active, a warning should show in the widget if it's not. You can launch it Manually from the Downloaded "Launcher" Helper GT folder.
3. **Action:** The Launcher executes the requested task silently in the background without interrupting the user's game.
   
---

## 🛠 Prerequisites for Development

If you are building GameTask from source, ensure your development environment is set up with the following:

* **Visual Studio 2022** (Community, Professional, or Enterprise)
* **.NET Desktop Development** workload
* **Universal Windows Platform (UWP) Development** workload
* **Windows 10/11 SDK** (Targeting a minimum of Windows 10 version 2004 / Build 19041)
* **Microsoft.Gaming.XboxGameBar** NuGet package installed

---

## 🚀 Building and Deployment

### 1. Build the Solution
1. Clone the repository and open `GameTask for Game Bar.sln` in Visual Studio 2022.
2. Allow Visual Studio to restore the required NuGet packages.
3. Select your target architecture from the build dropdown. (GameTask natively supports `x64`, `x86`, `ARM`, and `ARM64`).
4. Build the solution by pressing `Ctrl + Shift + B`.

### 2. Local Deployment & Testing
To test the widget in your own Game Bar:
1. Ensure **Xbox Game Bar Developer Mode** is enabled in your Windows Developer Settings.
2. In the Visual Studio Solution Explorer, right-click the **WidgetTemplate** project and select **Deploy**.
3. Press `Win + G` to open the Game Bar.
4. Open the **Widget Menu** (the list icon at the top of the screen) and select **GameTask** to pin or view the overlay.

---


---

## 🤝 Contributing

We welcome pull requests for bug fixes, new features, and UI improvements. 
1. Fork the repository.
2. Create a feature branch (`git checkout -b feature










