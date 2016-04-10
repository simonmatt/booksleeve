What is next?

# v1.1 #

(released, 17 Aug 2011)

  * re-designed public API to make it clear when you are working with lists/sets/sorted-sets/etc
  * complete the API - add any missing commands (unless there are good reasons not to, such as blpop which would upset a multiplexer)
  * overhaul of the "command" code (entirely internal) - to reduce maintenance cost and the "ugly" factor

# vNext #

Vague and woolly plans; don't mortgage your house on these...

  * move the existing stackexchange L1/L2 layered cache code into BookSleeve
  * connection pooling / automated connection recovery
  * support for the vNext redis features