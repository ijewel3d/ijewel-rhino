# iJewelViewer Plugin for Rhino

## Overview
iJewelViewer is a crossplatform C# plugin for Rhino, designed to simplify the designer role by enabling users to export models from Rhino and view them in iJewelViewer. This plugin integrates a HTTP server and a web view within Rhino, facilitating the display and interaction with iJewel Viewer.
The models can be rendered to images, videos or shared online.

## Features
- **Model Exporting:** Allows exporting Rhino models to a specified directory.
- **HTTP Server Integration:** Hosts a local HTTP server to serve exported models and other resources.
- **Web View Display:** Incorporates a web view for displaying HTML content, such as the exported model.
- **Dynamic File Serving:** Serves files based on HTTP requests, supporting various content types.
- **Experience seamless viewing of your 3D jewelry models directly within Rhino.
- **Export your Rhino models directly to the iJewel Viewer.
- **Provides different options within the viewer to rotate, zoom, and pan around your imported 3D model.
- **Create and customize your own configurator using a predefined set of metal and gem libraries, applying selected materials to your models according to your preferences.
- **Enhance your models further by applying different metal and gem environment maps available from the library.
- **Adjust advanced settings like environment rotation and intensity to achieve the perfect look for your designs.
- **Share your creations with clients, allowing them to view and interact with your designs in real-time, thereby providing an engaging experience.

## Installation
1. Ensure Rhino is installed on your system.
2. Download the iJewelViewer plugin.
3. Drag and drop the .rhp and .rui file inside the Rhino window Or you can directly install the plugin via Rhino Packagemenager, typing "iJewelViewer" and installing the plugin.

## Usage
- After installation, use the `iJewelViewer` Or `iJewelDesign` command within Rhino to export and view your 3D model.
- Access the served model through the launched iJewel Viewer, edit materials, and share with anyone across the world.

## Building and Customization

### Building the Plugin
1. Open the solution in Visual Studio.
2. Ensure all dependencies are correctly referenced.
3. Build the solution to generate the plugin file.

### Customizing UI Elements
- **Changing Icons:**
  1. Create new icon images.
  2. Replace existing icon files in the project resources.
  3. Rebuild the plugin to reflect these changes.

- **Toolbar Configuration:**
  1. Icons are registered with Rhino through the Rhino toolbar editor.
  2. Access the toolbar editor via Rhino's `Tools` menu.
  3. Customize toolbars by clinking the 'edit' button, which allow you to perform actions such as adding new buttons and assigning them to the `iJewelViewer` or `iJewelDesign` command.

## FAQs

### Where are the icons registered with Rhino for creating a toolbar?
Icons are registered through the Rhino toolbar editor, accessible via Rhino's `Tools` menu.
Write the command 'toorlbar' in Rhino.
Or click "options" and you will find toolbars there.

### How can we configure the toolbars in Rhino?
Toolbars can be configured in Rhino using the toolbar editor. You can add new buttons, assign commands, and customize the layout to suit your workflow.
Write the command 'toolbar' in Rhino.
Or click "options" and you will find toolbars there.
Then click "edit" toolbar
