# Damebooru Lookup

Adds "Look up in Damebooru" to the right-click menu of images. The image is downloaded by the extension (with the page's cookies and Referer, so hotlink-protected hosts work), then handed to `/duplicates/lookup` in a new tab.

## Install

1. Open `chrome://extensions` and enable Developer mode.
2. Load unpacked → pick this folder.
3. Open the extension's options and set your Damebooru URL (default `http://localhost:8080`).
