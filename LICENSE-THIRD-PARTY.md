# Third-party notices

The service ships the data below under its own license. The rest of this repository is under the GNU Affero General Public License (`LICENSE`).

## List of Dirty, Naughty, Obscene, and Otherwise Bad Words

- Title: "List of Dirty, Naughty, Obscene, and Otherwise Bad Words", by LDNOOBW and contributors
- Source: https://github.com/LDNOOBW/List-of-Dirty-Naughty-Obscene-and-Otherwise-Bad-Words, commit `5faf2ba42d7b1c0977169ec3611df25a3c08eb13`, retrieved 2026-11-18
- License: Creative Commons Attribution 4.0 International (CC BY 4.0), https://creativecommons.org/licenses/by/4.0/; the license text is in `GenOnlineService/data/textrules/LDNOOBW-LICENSE`
- Used in: `GenOnlineService/data/textrules/ldnoobw-en.txt` (from `en`) and `GenOnlineService/data/textrules/ldnoobw-ar.txt` (from `ar`), the seed words of the text rules (`docs/realtime-protocol.md` section 5.1a)
- Modified: deduplicated and normalized. Entries are trimmed, lower case and NFC; entries the service's own lists already hold were dropped (the service's entry wins); every entry got category `profanity`, severity `flag` and a match mode (`word` for phrases and for English words under 7 letters and Arabic words under 5, `anywhere` otherwise). Left out:
  - entries the text folding cannot read as written (digits read as leetspeak, symbols part words): `2g1c`, `2 girls 1 cup`, `rosy palm and her 5 sisters`, `s&m`, the middle finger emoji, and `xx`
  - entries game chat uses innocently: `escort`, `domination`, `hardcore`, `hard core`, `snowballing`, `suck`, `sucks`, `big black`, `girl on`, `huge fat`, `how to kill`, `how to murder`, `hot chick`, `tied up`, `taste my`, `tongue in a`, `tight white`, `tainted love`, `jelly donut`, `make me come`, `wrapping men`
  - Arabic entries that are common innocent words: فرج (also a given name), مبادل, بيضان
