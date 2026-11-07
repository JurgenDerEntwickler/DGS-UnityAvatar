// DGS MetaHuman - receives DGS sign sequences from the Python backend and plays them.
//
// Python (DGSNEW backend/live_sign_avatar.py, or web_app.py --unity-editor) runs a TCP
// server on 127.0.0.1:8765 and sends ONE JSON line per sentence:
//   {"original_text": "...", "glosses": [...],
//    "signs": [{"gloss": "HALLO", "animation": "hallo", "has_animation": true,
//               "fingerspell": false, "duration": 1.2}, ...]}
// This actor connects to it (like the Unity Editor does), queues the signs and plays the
// matching animation for each gloss on the MetaHuman's Body:
//   * sign -> sign: blends straight from one sign into the next (no drop to rest),
//   * holds the last pose briefly, then blends back to the idle,
//   * speeds up gently when signs pile up (lecturer faster than the avatar),
//   * words without a clip: short pause (Python's "duration"),
//   * fingerspelling: plays FS_A, FS_B, ... if all letters exist in SignClips.
//
// Requirements on the avatar: the Body uses an Animation Blueprint with a
// Slot 'DefaultSlot' node (idle -> Slot -> Output Pose). See the setup guide.

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "SignReceiver.generated.h"

class FSocket;
class UAnimInstance;
class UAnimMontage;
class UAnimSequenceBase;
class USkeletalMeshComponent;

/** One sign as sent by the Python backend. */
USTRUCT(BlueprintType)
struct FSignEntry
{
	GENERATED_BODY()

	UPROPERTY(BlueprintReadOnly, Category = "DGS")
	FString Gloss;

	UPROPERTY(BlueprintReadOnly, Category = "DGS")
	FString Animation;

	UPROPERTY(BlueprintReadOnly, Category = "DGS")
	bool bHasAnimation = false;

	UPROPERTY(BlueprintReadOnly, Category = "DGS")
	bool bFingerspell = false;

	/** Seconds to show a sign that has no clip (set by Python). */
	UPROPERTY(BlueprintReadOnly, Category = "DGS")
	float Duration = 1.2f;
};

/** One entry of the sign library: which animation to play for a gloss, and how. */
USTRUCT(BlueprintType)
struct FSignClip
{
	GENERATED_BODY()

	/** Retargeted MetaHuman animation (MH_...). */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "DGS")
	TObjectPtr<UAnimSequenceBase> Anim = nullptr;

	/** Where the sign really starts in the clip (0..1). Skips the hands rising from rest. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "DGS", meta = (ClampMin = "0", ClampMax = "1"))
	float InPoint = 0.f;

	/** Where the sign really ends in the clip (0..1). Skips the hands dropping back to rest. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "DGS", meta = (ClampMin = "0", ClampMax = "1"))
	float OutPoint = 1.f;

	/** Playback speed of this sign (1 = as recorded). */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "DGS", meta = (ClampMin = "0.25", ClampMax = "3"))
	float PlayRate = 1.f;
};

UCLASS()
class DGSMETAHUMAN_API ASignReceiver : public AActor
{
	GENERATED_BODY()

public:
	ASignReceiver();

	// ---------------------------------------------------------------- connection

	UPROPERTY(EditAnywhere, Category = "DGS|Connection")
	FString Host = TEXT("127.0.0.1");

	UPROPERTY(EditAnywhere, Category = "DGS|Connection")
	int32 Port = 8765;

	/** Seconds between connection attempts while Python is not running. */
	UPROPERTY(EditAnywhere, Category = "DGS|Connection", meta = (ClampMin = "0.5"))
	float ReconnectDelay = 2.f;

	// ---------------------------------------------------------------- avatar

	/** The MetaHuman in the level (BP_Signer). */
	UPROPERTY(EditAnywhere, Category = "DGS|Avatar")
	TObjectPtr<AActor> Avatar = nullptr;

	/** Name of the body skeletal mesh component in the MetaHuman Blueprint. */
	UPROPERTY(EditAnywhere, Category = "DGS|Avatar")
	FName BodyComponentName = TEXT("Body");

	/** Slot node in the body's Animation Blueprint. */
	UPROPERTY(EditAnywhere, Category = "DGS|Avatar")
	FName SlotName = TEXT("DefaultSlot");

	// ---------------------------------------------------------------- signs

	/** Gloss (e.g. HALLO, case does not matter) -> animation. Letters for fingerspelling: FS_A, FS_B, ... */
	UPROPERTY(EditAnywhere, Category = "DGS|Signs")
	TMap<FString, FSignClip> SignClips;

	UPROPERTY(EditAnywhere, Category = "DGS|Signs")
	FString LetterPrefix = TEXT("FS_");

	UPROPERTY(EditAnywhere, Category = "DGS|Signs", meta = (ClampMin = "0.1"))
	float SecondsPerLetter = 0.3f;

	// ---------------------------------------------------------------- blending (seconds)

	/** Blend from one sign into the next. 0.12-0.25 looks natural. */
	UPROPERTY(EditAnywhere, Category = "DGS|Blending", meta = (ClampMin = "0.02", ClampMax = "1"))
	float SignToSignBlend = 0.18f;

	/** Blend from rest (idle) into the first sign of a sentence. */
	UPROPERTY(EditAnywhere, Category = "DGS|Blending", meta = (ClampMin = "0.02", ClampMax = "1.5"))
	float FromRestBlend = 0.3f;

	/** Blend back to rest after the last sign. */
	UPROPERTY(EditAnywhere, Category = "DGS|Blending", meta = (ClampMin = "0.02", ClampMax = "2"))
	float ToRestBlend = 0.5f;

	/** Hold the last pose this long before going back to rest (the next sentence often comes in time). */
	UPROPERTY(EditAnywhere, Category = "DGS|Blending", meta = (ClampMin = "0", ClampMax = "3"))
	float HoldBeforeRest = 0.7f;

	/** Blend for words without a clip (back to the idle while waiting). */
	UPROPERTY(EditAnywhere, Category = "DGS|Blending", meta = (ClampMin = "0.02", ClampMax = "1.5"))
	float PauseBlend = 0.35f;

	UPROPERTY(EditAnywhere, Category = "DGS|Blending", meta = (ClampMin = "0.02", ClampMax = "0.5"))
	float LetterBlend = 0.08f;

	// ---------------------------------------------------------------- tempo

	/** Fastest playback when many signs are waiting. 1 = never speed up. */
	UPROPERTY(EditAnywhere, Category = "DGS|Tempo", meta = (ClampMin = "1", ClampMax = "2"))
	float MaxTempo = 1.35f;

	/** Number of waiting signs at which MaxTempo is reached. */
	UPROPERTY(EditAnywhere, Category = "DGS|Tempo", meta = (ClampMin = "2"))
	int32 BacklogForMaxTempo = 8;

	// ---------------------------------------------------------------- API

	/** Queue the signs of one JSON message (same format Python sends). Used by TCP and later by Pixel Streaming. */
	UFUNCTION(BlueprintCallable, Category = "DGS")
	void EnqueueJson(const FString& Json);

	/** Fired when a sign starts (for UI / web page feedback). */
	UFUNCTION(BlueprintImplementableEvent, Category = "DGS")
	void OnSignStarted(const FSignEntry& Sign);

	/** Fired when all queued signs are done. */
	UFUNCTION(BlueprintImplementableEvent, Category = "DGS")
	void OnQueueEmpty();

	UFUNCTION(BlueprintPure, Category = "DGS")
	bool IsConnected() const { return Socket != nullptr; }

protected:
	virtual void BeginPlay() override;
	virtual void EndPlay(const EEndPlayReason::Type EndPlayReason) override;
	virtual void Tick(float DeltaSeconds) override;

private:
	// connection
	void TickConnection(float DeltaSeconds);
	void StartConnect();
	void ReadSocket(float DeltaSeconds);
	static void CloseSocket(FSocket*& S);

	FSocket* Socket = nullptr;
	FSocket* ConnectingSocket = nullptr;
	float ConnectTimer = 0.f;
	float ReconnectTimer = 0.f;
	float AliveCheckTimer = 0.f;
	TArray<uint8> Pending;

	// playback
	enum class EState : uint8 { Rest, Playing, Holding, Pausing, Spelling };

	void TickPlayback(float DeltaSeconds);
	void StartNext(UAnimInstance* Anim);
	void PlayClip(UAnimInstance* Anim, const FSignClip& Clip, float BlendIn);
	void GoToRest(UAnimInstance* Anim, float Blend);
	const FSignClip* FindClip(const FString& Key) const;
	UAnimInstance* GetAnim();

	EState State = EState::Rest;
	TArray<FSignEntry> Queue;
	TArray<FSignClip> SpellClips;
	int32 LetterIndex = 0;
	float StateTimer = 0.f;
	float CurrentOutSeconds = 0.f;
	float CurrentRate = 1.f;
	float Tempo = 1.f;
	bool bWasBusy = false;
	bool bWarnedNoAnim = false;

	UPROPERTY(Transient)
	TObjectPtr<UAnimMontage> CurrentMontage = nullptr;

	TWeakObjectPtr<USkeletalMeshComponent> Body;
};
