import AVFoundation
import UniformTypeIdentifiers

// What does AVFoundation actually decode on this machine? The Windows engine hard-codes
// mp3/aac/m4a/flac/alac/wav; the Mac engine must supply its own list, and guessing it
// would silently drop local files from the index or force needless transcodes.

let types = AVURLAsset.audiovisualTypes()
print("AVURLAsset.audiovisualTypes(): \(types.count) UTIs\n")

// Map each UTI to its file extensions, which is the form Core wants.
var exts = Set<String>()
for t in types {
    guard let ut = UTType(t.rawValue) else { continue }
    for e in ut.tags[.filenameExtension] ?? [] { exts.insert(e.lowercased()) }
}

let windows = ["mp3", "aac", "m4a", "flac", "alac", "wav"]
print("Windows list, checked against AVFoundation:")
for w in windows {
    print("  \(w.padding(toLength: 6, withPad: " ", startingAt: 0)) \(exts.contains(w) ? "yes" : "NO")")
}

let audioish = ["mp3","m4a","aac","flac","wav","aiff","aif","alac","caf","opus","ogg","oga","wma","ape","wv","mka","m4b","amr","au","mp2","ac3","eac3","dsf","mpc"]
print("\nAudio extensions AVFoundation reports:")
print("  " + audioish.filter { exts.contains($0) }.sorted().joined(separator: ", "))
print("\nAsked for but NOT reported:")
print("  " + audioish.filter { !exts.contains($0) }.sorted().joined(separator: ", "))
