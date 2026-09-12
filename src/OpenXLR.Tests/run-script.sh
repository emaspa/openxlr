#!/bin/sh
# The one program the test assembly runs that it did not write itself. What a
# fake helper does lives in a plain text file next to the link that started
# this, and this hands that file to the shell. See ExecutableScript.cs.
self=$0
case $self in
  */*) ;;
  *) self=$(command -v -- "$self") ;;
esac
exec /bin/sh "$self.body" "$@"
