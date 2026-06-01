# Toolup-forge — Icon Set

Sibling identity for the Toolup-forge OSS project: the ToolUp brand mark wrapped
in code-style brackets ⟨ ⟩ to signal "developer / open source", with a /forge
wordmark tag.

## Contents

favicon/        Browser tab icons (transparent + flat-dark), 16–64px
apple/          Apple touch icons (152 / 167 / 180px), gradient, opaque
pwa/            Progressive-web-app icons 192 / 512px, plus maskable variants
repo/           GitHub avatar & large icons — "mark" (square crop friendly)
                and "wordmark" (stacked, for square cards). Gradient / flat /
                light / transparent backgrounds.
social/         1280×640 social-preview banners (horizontal lockup)
svg/            Scalable master (vector brackets + embedded icon)
site.webmanifest    Drop-in PWA manifest referencing the pwa/ icons
head-snippet.html   Paste into your <head> (adjust paths to taste)

## Which to use where

- GitHub org / repo avatar  → repo/icon-mark-1024.png  (GitHub circle-crops it)
- Social preview card       → social/social-preview-1280x640.png
- Website favicon           → favicon/* + head-snippet.html
- iOS home screen           → apple/apple-touch-icon.png
- Android / PWA install     → pwa/* + site.webmanifest

## Colors

Purple (brand)   #7A3BD9 → #D8A6F2
Amber (forge)    #E0521A → #FFC56B
Bracket neutral  #5B5560
Ink              #0A0A0C
Paper            #F4F2F0
