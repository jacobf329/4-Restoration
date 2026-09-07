Menu backdrops.

Drop images in this folder with these exact names. .png, .jpg, .jpeg and .webp all work.

  title.png     Title screen
  mode.png      Mode select
  lobby.png     Lobby (player slots)
  options.png   Options, Controls and Rebind
  results.png   End-of-match standings

Any that are missing fall back to the procedural background, so you can add them one at a
time and the game keeps working throughout.

Loaded at runtime rather than through Godot's import pipeline, so a new file takes effect on
the next launch -- you do not need to open the editor.

Landscape, roughly 16:9. They are cover-fitted and centre-cropped to whatever the window is,
so keep anything important away from the extreme left and right edges.

They are darkened in-game: a light overall dim, heavier washes under the header and the hint
bar, and a soft band down the middle where the menu rows sit. Dark, low-contrast source art
works best -- anything bright behind centred text will fight it.
