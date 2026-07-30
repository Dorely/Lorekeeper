# Semantic editor third-party notices

The checked-in browser bundle is built from the exact versions in
`package-lock.json`. Runtime packages are MIT licensed:

- `prosemirror-commands` 1.7.1
- `prosemirror-history` 1.5.0
- `prosemirror-keymap` 1.2.3
- `prosemirror-model` 1.25.11
- `prosemirror-state` 1.4.4
- `prosemirror-transform` 1.12.0
- `prosemirror-view` 1.42.2
- `orderedmap` 2.1.1
- `rope-sequence` 1.3.4
- `w3c-keyname` 2.2.8

`esbuild` 0.28.1 is an MIT-licensed build-time dependency and is not included
as executable code in the shipped browser bundle. `jsdom` 26.1.0 and its
integrity-locked transitive graph are test-only dependencies and are likewise
not shipped in the browser bundle.

The package tarballs and their individual `LICENSE` files are resolved by
`npm ci` from the integrity-pinned lock file. ProseMirror is copyright
Marijn Haverbeke and other contributors.

## MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
