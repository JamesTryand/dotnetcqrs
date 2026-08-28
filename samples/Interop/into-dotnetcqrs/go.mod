// dotnetcqrs-multi-node Milestone 5, direction B driver. Stdlib only, its own
// throwaway module: it lives in the dotnetcqrs repo (a different Go module than
// pocketcqrs) and pocketcqrs's gatewayclient is under internal/, so the request
// construction is mirrored here by hand rather than imported. See main.go.
module interop-into-dotnetcqrs

go 1.26
