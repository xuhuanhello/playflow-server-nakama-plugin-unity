// Regenerate with: go run generate.go > admission-v1.json
// Fixed public test key only. Never use it in a deployment.
package main

import (
    "crypto/hmac"
    "crypto/sha256"
    "encoding/base64"
    "encoding/json"
    "os"
)

type claims struct {
    SchemaVersion int `json:"schema_version"`
    UserID string `json:"user_id"`
    WorkerID string `json:"worker_id"`
    BootID string `json:"boot_id"`
    RoomID string `json:"room_id"`
    AllocationID string `json:"allocation_id"`
    ReservationID string `json:"reservation_id"`
    Seat int `json:"seat"`
    Epoch int64 `json:"epoch"`
    ExpiresAt int64 `json:"exp"`
    Nonce string `json:"nonce"`
    Resume bool `json:"resume"`
}

func main() {
    key := make([]byte, 32)
    for i := range key { key[i] = byte(i) }
    raw, err := json.Marshal(claims{1,"user-a","worker-a","boot-a","room-a","allocation-a","reservation-a",0,1,1700000060,"nonce-fixture-0001",false})
    if err != nil { panic(err) }
    payload := base64.RawURLEncoding.EncodeToString(raw)
    h := hmac.New(sha256.New, key)
    _, _ = h.Write([]byte(payload))
    fixture := struct {
        Key string `json:"key_base64url"`
        Now int64 `json:"now"`
        Token string `json:"token"`
    }{base64.RawURLEncoding.EncodeToString(key),1700000000,payload+"."+base64.RawURLEncoding.EncodeToString(h.Sum(nil))}
    encoder := json.NewEncoder(os.Stdout)
    encoder.SetIndent("", "  ")
    if err := encoder.Encode(fixture); err != nil { panic(err) }
}
