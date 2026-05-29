# GameTask for Xbox Game Bar

**GameTask** is a custom Xbox Game Bar extension designed to bring seamless task management and utility controls directly into your gaming overlay. 

Access your essential tasks without ever alt-tabbing or leaving your game.


<img width="217" height="488,5" alt="image" src="https://github.com/user-attachments/assets/829a99de-6247-4ab3-adc5-8c4ba2ce4928" />

---

## 🥜 In a nutshell

1. 🎮 **Controller-first design :** The app is thought out for maximum ease-of-use when using a controller.
2. ▶️ **Launch and Kill apps:** The core feature is to be able to Launch and Kill apps with a single press of the Action Buttons.
3. 🪟 **Focus and Fullscreen :** Bring certain apps to front, and send Custom Keys or Maximise command to make them occupy the whole screen.
4. 🔧 **Behavior System:** For each main button, you can decide additional subsequent Behaviors, like "Stay on GameBar", Exit "GameBar", "Focus App" and more.
5. 🚀 **Pin Launched Services:** Option to automatically pin launched services to the top of the Widget for convenience.
6. 📁 **Multipath setup:** Some apps launch with an .exe, but switch window later.  Multipath allows targeting different services for the "same" app.
7. 🎨 **Customisation options :**  Use custom icons, Emojis, or the .exe's default icons. Rename apps, and move them around.
8. 🏷️ **Categories:** Categorise your apps and games however you want, assigning custom colors, icons and behaviors.


   <img width="448" height="352" alt="image" src="https://github.com/user-attachments/assets/bd207f18-61de-4466-87b5-bdf4771c3b79" />

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
   
<img width="440" height="230" alt="image" src="https://github.com/user-attachments/assets/def4d0f6-8951-4b05-b33e-467b01d58f76" />

4. **Action:** The Launcher executes the requested task silently in the background without interrupting the user's game.
   
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










