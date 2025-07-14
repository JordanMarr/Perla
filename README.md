# Perla Dev Server [![wakatime](https://wakatime.com/badge/user/4537232c-b581-465b-9604-b10a55ffa7b4/project/d46e17c5-054e-4249-a2ab-4294d0e5e026.svg)](https://wakatime.com/badge/user/4537232c-b581-465b-9604-b10a55ffa7b4/project/d46e17c5-054e-4249-a2ab-4294d0e5e026)

[![Open in GitHub Codespaces](https://github.com/codespaces/badge.svg)](https://codespaces.new/AngelMunoz/Perla/tree/dev?quickstart=1) [![Open in Gitpod](https://gitpod.io/button/open-in-gitpod.svg)](https://gitpod.io/#https://github.com/AngelMunoz/Perla)

Perla is a cross-platform single executable binary CLI Tool for a Development Server of Single Page Applications (like vite/webpack but no node required!).

## Installation

You can install Perla manually by downloading the latest release from the [releases page](https://github.com/AngelMunoz/Perla/releases/latest) and adding it to your path.

If you prefer a more automated approach, you can use the provided install scripts, they're available on this repository for you to review the contents before running them.

For Linux or macOS, you can run the following command in your terminal:

```bash
curl -fsSL https://raw.githubusercontent.com/AngelMunoz/Perla/dev/install.sh | bash
```

For Windows (or Linux, MacOS if you've installed pwsh), you can use powershell to run the install script:

```powershell
iwr https://raw.githubusercontent.com/AngelMunoz/Perla/dev/install.ps1 -UseBasicParsing | iex
```

If you prefer to use the dotnet (global | local) tool, you can install it using the following command:

```bash
dotnet tool install --global Perla
```

Once you're done with the installation, you can run `perla --help` to verify that the installation was successful.

```
Description:
  The Perla Dev Server!

Usage:
  Perla [command] [options]

Options:
  -?, -h, --help  Show help and usage information
  --version       Show version information

Commands:
  create, generate, n, new <name>        Creates a new project based on the selected template if it exists
  install                                Installs the project dependencies from the perla.json file
  add <package>                          Adds a package to the project dependencies
  remove <package>                       Removes a package from the project dependencies
  list, ls                               Lists the current dependencies in a table or an npm style json string
  s, serve, start                        Starts the development server and if fable projects are present it also takes care of it.
  b, build                               Builds the SPA application for distribution
  t, templates <TemplateRepositoryName>  Handles Template Repository operations such as list, add, update, and remove templates []
  describe, ds <properties>              Describes the perla.json file or it's properties as requested
```

## Existing tools

If you actually use and like nodejs, then you would be better taking a look at the tools that inspired this repository

- [jspm](https://github.com/jspm/jspm-cli) - Import map handling, they are the best at manipulating import maps :heart:
- [vite](https://vitejs.dev/)
- [snowpack](https://www.snowpack.dev/)

These tools have a bigger community and rely on an even bigger ecosystem plus they support plugins via npm so if you're using node stick with them they are a better choice
Perla's unbundled development was inspired by both snowpack and vite, CDN dependencies were inspired by snowpack's remote sources development
