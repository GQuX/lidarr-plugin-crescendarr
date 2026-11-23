# Third-party notices

The Crescendarr audit-only beta DLL bundles **no** third-party code. It is compiled from this repository only (BSD
Zero Clause License, see `LICENSE`).

At runtime it links against assemblies supplied by the Lidarr installation it is loaded into, and does not redistribute
them:

| Component | Supplied by | License |
|---|---|---|
| Lidarr.Core, Lidarr.Common | Lidarr v3.1.6.5079 | GPL-3.0 (Lidarr project) |
| NLog | Lidarr installation | BSD-3-Clause |
| FluentValidation | Lidarr installation | Apache-2.0 |
| .NET 8 base class library | .NET runtime used by Lidarr | MIT |

Lidarr is a trademark of its respective owners. Crescendarr is not affiliated with or endorsed by the Lidarr project.
