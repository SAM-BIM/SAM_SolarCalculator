[![Build](https://github.com/SAM-BIM/SAM_SolarCalculator/actions/workflows/build.yml/badge.svg?branch=master)](https://github.com/SAM-BIM/SAM_SolarCalculator/actions/workflows/build.yml)
[![Installer (latest)](https://img.shields.io/github/v/release/SAM-BIM/SAM_Deploy?label=installer)](https://github.com/SAM-BIM/SAM_Deploy/releases/latest)

# SAM_SolarCalculator

<a href="https://github.com/SAM-BIM/SAM">
  <img src="https://github.com/SAM-BIM/SAM/blob/master/Grasshopper/SAM.Core.Grasshopper/Resources/SAM_Small.png"
       align="left" hspace="10" vspace="6">
</a>

**SAM_SolarCalculator** is part of the **SAM (Sustainable Analytical Model) Toolkit** —  
an open-source collection of tools designed to help engineers create, manage,
and process analytical building models for energy and environmental analysis.

This repository provides **solar irradiance and shading calculation utilities**
used to support solar-driven energy modelling and environmental analysis workflows
within the SAM ecosystem.

---

## Features

- Calculation of solar irradiance on analytical surfaces  
- Evaluation of solar shading and self-shading effects  
- Generation of energy model inputs based on calculated solar exposure  

---

## Resources
- 📘 **SAM Wiki:** https://github.com/SAM-BIM/SAM/wiki  
- 🧠 **SAM Core:** https://github.com/SAM-BIM/SAM  
- 🧰 **Installers:** https://github.com/SAM-BIM/SAM_Deploy  

---

## Installing

To install **SAM** using the Windows installer, download and run the  
[latest installer](https://github.com/SAM-BIM/SAM_Deploy/releases/latest).

Alternatively, you can build the toolkit from source using Visual Studio.  
See the main repository for details:  
👉 https://github.com/SAM-BIM/SAM

---

## Development notes

- Target framework: **.NET / C#**
- Coding style follows standard SAM-BIM conventions.
- New or modified `.cs` files must include the SPDX header from `COPYRIGHT_HEADER.txt`

---

## Tests

Macro/integration tests live in **[`SAM_SolarCalculator.Tests`](SAM_SolarCalculator.Tests/README.md)**
(xUnit, .NET 8). They run against real exported `.sam` models and guard the
SAM-vs-TAS solar-coverage benchmark (e.g. SAM reproduces TAS shading to ~0.9%).

```bash
dotnet test SAM_SolarCalculator/SAM_SolarCalculator.Tests
```

Run this before merging changes to the solar-calculation code. They are not yet
wired into CI — see the [tests README](SAM_SolarCalculator.Tests/README.md#ci-current-status--best-practice)
for how to add a `dotnet test` step so they run on every PR.

---

## Licence

This repository is free software licensed under the  
**GNU Lesser General Public License v3.0 or later (LGPL-3.0-or-later)**.

Each contributor retains copyright to their respective contributions.  
The project history (Git) records authorship and provenance of all changes.

See:
- `LICENSE`
- `NOTICE`
- `COPYRIGHT_HEADER.txt`