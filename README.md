# iJewelViewer Plugin for Rhino

## Overview
iJewelViewer is a crossplatform C# plugin for Rhino, designed to simplify the designer role by enabling users to export models from Rhino and view them in iJewelViewer. This plugin integrates a HTTP server and a web view within Rhino, facilitating the display and interaction with iPixel website in a more accessible and interactive format.

## Features
- **Model Exporting:** Allows exporting Rhino models to a specified directory.
- **HTTP Server Integration:** Hosts a local HTTP server to serve exported models and other resources.
- **Web View Display:** Incorporates a web view for displaying HTML content, such as the exported model.
- **Dynamic File Serving:** Serves files based on HTTP requests, supporting various content types.

## Installation
1. Ensure Rhino is installed on your system.
2. Download the iJewelViewer plugin.
3. Drag and drop the .rhp and .rui file inside the Rhino window Or you can directly install the plugin via Rhino Packagemenager, typing "iJewelViewer" and installing the plugin.

## Usage
- After installation, use the `iJewelViewer` command within Rhino to export and view your 3D model.
- Access the served model through the provided web view or via a web browser for online editing.

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
  3. Customize toolbars by clinking the 'edit' button, which allow you to perform actions such as adding new buttons and assigning them to the PixotronicsCommand commands.

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
