# Third-party data

## IEEE OUI registry (`GenOnlineService/data/oui.tsv`)

The MAC vendor names of the staff pages come from the public listings of the IEEE Registration Authority:

- MA-L: https://standards-oui.ieee.org/oui/oui.csv
- MA-M: https://standards-oui.ieee.org/oui28/mam.csv
- MA-S: https://standards-oui.ieee.org/oui36/oui36.csv

`scripts/update-oui.py` downloads them and keeps only the assigned prefix and the organization name. The first line of the
file records the retrieval date. The listings remain the IEEE's; their use is subject to the terms of use of the IEEE
website (https://www.ieee.org/site-terms-conditions.html).

## MaxMind GeoLite2 (`GenOnlineService/data/GeoLite2-*.mmdb`)

Not part of this repository or its image. The deployment downloads GeoLite2-City (and GeoLite2-ASN when configured) with
geoipupdate under the MaxMind GeoLite2 End User License Agreement (https://www.maxmind.com/en/geolite2/eula), which
requires this notice: This product includes GeoLite2 data created by MaxMind, available from https://www.maxmind.com.
