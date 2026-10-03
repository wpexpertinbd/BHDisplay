#!/bin/zsh
# Runs the Swift and C# protocol implementations against each other, both directions.
set -u
cd "${0:A:h}"
DOTNET=${DOTNET:-/usr/local/share/dotnet/dotnet}
swiftc -D BHDS_TESTS ../../Sources/ShareCore.swift ../../Sources/ShareNet.swift swift/main.swift -o /tmp/swiftpeer || exit 1
$DOTNET build csharp -c Release -o /tmp/cspeer -v q -nologo >/dev/null || { $DOTNET build csharp -c Release -o /tmp/cspeer -nologo | tail -20; exit 1; }
fail=0
round() {   # $1 = listener cmd, $2 = dialer cmd, $3 = label, $4 = port (own port per round: avoids TIME_WAIT)
  local L=/tmp/interop-L.$$ D=/tmp/interop-D.$$
  eval "$1 listen $4" > $L 2>&1 & local lp=$!
  sleep 1.5
  eval "$2 dial 127.0.0.1 $4" > $D 2>&1; local dr=$?
  wait $lp; local lr=$?
  local lc=$(grep '^CODE' $L | cut -d' ' -f2) dc=$(grep '^CODE' $D | cut -d' ' -f2)
  echo "== $3: listener exit $lr, dialer exit $dr, codes $lc / $dc"
  grep -E '^(PEER|GOT|CLOSED|ERROR|TIMEOUT)' $L | sed 's/^/   L /'; grep -E '^(PEER|GOT|CLOSED|ERROR|TIMEOUT)' $D | sed 's/^/   D /'
  [[ $lr == 0 && $dr == 0 && -n $lc && $lc == $dc ]] || fail=1
  rm -f $L $D
}
BASE=$(( 25000 + RANDOM % 4000 ))
round "/tmp/swiftpeer" "$DOTNET /tmp/cspeer/InteropPeer.dll" "Swift listens, C# dials" $BASE
round "$DOTNET /tmp/cspeer/InteropPeer.dll" "/tmp/swiftpeer" "C# listens, Swift dials" $((BASE + 1))
[[ $fail == 0 ]] && echo "INTEROP PASSED" || echo "INTEROP FAILED"
exit $fail
