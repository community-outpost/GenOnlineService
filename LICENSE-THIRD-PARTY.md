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

## IEEE OUI registry (`GenOnlineService/data/oui.tsv`)

The MAC vendor names of the staff pages come from the public listings of the IEEE Registration Authority:

- MA-L: https://standards-oui.ieee.org/oui/oui.csv
- MA-M: https://standards-oui.ieee.org/oui28/mam.csv
- MA-S: https://standards-oui.ieee.org/oui36/oui36.csv

`scripts/update-oui.py` downloads them and keeps only the assigned prefix and the organization name. The first line of the
file records the retrieval date. The listings remain the IEEE's; their use is subject to the terms of use of the IEEE
website (https://www.ieee.org/site-terms-conditions.html).

## flag-icons (`GenOnlineService/wwwroot/portal/flags`)

The country flags of the staff pages, served by the service itself:

- Title: "flag-icons", by Panayiotis Lipiridis and contributors
- Source: npm package `flag-icons` 7.6.0 (https://github.com/lipis/flag-icons), retrieved 2026-11-19
- License: MIT; the license text is in `GenOnlineService/wwwroot/portal/flags/LICENSE`
- Used: the 4:3 SVGs (`flags/4x3`) of the 250 codes `PortalText.CountryFlag` knows, unmodified

## MaxMind GeoLite2 (`GenOnlineService/data/GeoLite2-*.mmdb`)

Not part of this repository or its image. The deployment downloads GeoLite2-City (and GeoLite2-ASN when configured) with
geoipupdate under the MaxMind GeoLite2 End User License Agreement (https://www.maxmind.com/en/geolite2/eula), which
requires this notice: This product includes GeoLite2 data created by MaxMind, available from https://www.maxmind.com.
