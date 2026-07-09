Drop your logo files here to show them on the sign-in landing page:

    emydex.png      (or .svg / .jpg / .jpeg / .gif)
    mla.png         (or .svg / .jpg / .jpeg / .gif)

They are embedded into the landing page as base64 data URIs at runtime, so the
page stays fully self-contained (no external requests). If a file is missing,
the page falls back to a text placeholder for that logo.

These files are copied next to the built .exe (into an "assets" folder) by the
project, and LandingPage.cs reads them from there.
