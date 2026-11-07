#include "SignReceiver.h"

#include "AlphaBlend.h"
#include "Animation/AnimInstance.h"
#include "Animation/AnimMontage.h"
#include "Animation/AnimSequenceBase.h"
#include "Components/SkeletalMeshComponent.h"
#include "Dom/JsonObject.h"
#include "IPAddress.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Sockets.h"
#include "SocketSubsystem.h"

DEFINE_LOG_CATEGORY_STATIC(LogSign, Log, All);

ASignReceiver::ASignReceiver()
{
	PrimaryActorTick.bCanEverTick = true;
}

void ASignReceiver::BeginPlay()
{
	Super::BeginPlay();
	ReconnectTimer = 0.f;   // try to connect right away
	if (!Avatar)
	{
		UE_LOG(LogSign, Warning, TEXT("SignReceiver: no Avatar set. Select the SignReceiver and set Avatar to BP_Signer."));
	}
}

void ASignReceiver::EndPlay(const EEndPlayReason::Type EndPlayReason)
{
	CloseSocket(Socket);
	CloseSocket(ConnectingSocket);
	Super::EndPlay(EndPlayReason);
}

void ASignReceiver::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);
	TickConnection(DeltaSeconds);
	TickPlayback(DeltaSeconds);
}

// =====================================================================================
// Connection: non-blocking, so the game never freezes while Python is not running.
// =====================================================================================

void ASignReceiver::CloseSocket(FSocket*& S)
{
	if (S)
	{
		S->Close();
		if (ISocketSubsystem* SS = ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM))
		{
			SS->DestroySocket(S);
		}
		S = nullptr;
	}
}

void ASignReceiver::StartConnect()
{
	ISocketSubsystem* SS = ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM);
	if (!SS)
	{
		return;
	}

	TSharedRef<FInternetAddr> Addr = SS->CreateInternetAddr();
	bool bValid = false;
	Addr->SetIp(*Host, bValid);
	Addr->SetPort(Port);
	if (!bValid)
	{
		UE_LOG(LogSign, Warning, TEXT("SignReceiver: invalid host '%s'"), *Host);
		return;
	}

	ConnectingSocket = SS->CreateSocket(NAME_Stream, TEXT("DGS sign client"), Addr->GetProtocolType());
	if (!ConnectingSocket)
	{
		return;
	}
	ConnectingSocket->SetNonBlocking(true);
	ConnectingSocket->Connect(*Addr);   // returns at once; result is checked in TickConnection
	ConnectTimer = 0.f;
}

void ASignReceiver::TickConnection(float DeltaSeconds)
{
	if (Socket)
	{
		ReadSocket(DeltaSeconds);
		return;
	}

	if (ConnectingSocket)
	{
		ConnectTimer += DeltaSeconds;
		// Writable = the connection is established.
		if (ConnectingSocket->Wait(ESocketWaitConditions::WaitForWrite, FTimespan::Zero()))
		{
			Socket = ConnectingSocket;
			ConnectingSocket = nullptr;
			Pending.Reset();
			AliveCheckTimer = 0.f;
			UE_LOG(LogSign, Display, TEXT("Connected to Python server %s:%d"), *Host, Port);
		}
		else if (ConnectTimer > 3.f)
		{
			CloseSocket(ConnectingSocket);   // Python not running (yet): try again later
			ReconnectTimer = ReconnectDelay;
		}
		return;
	}

	ReconnectTimer -= DeltaSeconds;
	if (ReconnectTimer <= 0.f)
	{
		ReconnectTimer = ReconnectDelay;
		StartConnect();
	}
}

void ASignReceiver::ReadSocket(float DeltaSeconds)
{
	bool bClosed = false;

	uint32 Size = 0;
	while (Socket->HasPendingData(Size) && Size > 0)
	{
		const int32 Old = Pending.Num();
		Pending.AddUninitialized(static_cast<int32>(Size));
		int32 Read = 0;
		const bool bOk = Socket->Recv(Pending.GetData() + Old, static_cast<int32>(Size), Read);
		Pending.SetNum(Old + FMath::Max(Read, 0));
		if (!bOk)
		{
			bClosed = true;
			break;
		}
		if (Read <= 0)
		{
			break;
		}
	}

	// Detect a closed connection (Python stopped): a peek on a closed stream socket fails.
	AliveCheckTimer += DeltaSeconds;
	if (!bClosed && AliveCheckTimer > 0.5f)
	{
		AliveCheckTimer = 0.f;
		uint8 Dummy = 0;
		int32 Read = 0;
		if (!Socket->Recv(&Dummy, 1, Read, ESocketReceiveFlags::Peek))
		{
			bClosed = true;
		}
	}

	// One JSON message per line.
	int32 NewLine = INDEX_NONE;
	while (Pending.Find(static_cast<uint8>('\n'), NewLine))
	{
		FUTF8ToTCHAR Conv(reinterpret_cast<const ANSICHAR*>(Pending.GetData()), NewLine);
		FString Line(Conv.Length(), Conv.Get());
		Pending.RemoveAt(0, NewLine + 1);
		Line.TrimStartAndEndInline();
		if (!Line.IsEmpty())
		{
			EnqueueJson(Line);
		}
	}

	if (bClosed)
	{
		UE_LOG(LogSign, Display, TEXT("Python server disconnected - retrying every %.1f s"), ReconnectDelay);
		CloseSocket(Socket);
		ReconnectTimer = ReconnectDelay;
	}
}

// =====================================================================================
// Messages
// =====================================================================================

void ASignReceiver::EnqueueJson(const FString& Json)
{
	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
	if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
	{
		UE_LOG(LogSign, Warning, TEXT("SignReceiver: could not parse message: %s"), *Json.Left(200));
		return;
	}

	FString Original;
	Root->TryGetStringField(TEXT("original_text"), Original);

	const TArray<TSharedPtr<FJsonValue>>* Signs = nullptr;
	if (!Root->TryGetArrayField(TEXT("signs"), Signs) || !Signs)
	{
		UE_LOG(LogSign, Warning, TEXT("SignReceiver: message has no \"signs\" array"));
		return;
	}

	TArray<FString> Glosses;
	for (const TSharedPtr<FJsonValue>& Value : *Signs)
	{
		const TSharedPtr<FJsonObject>* Obj = nullptr;
		if (!Value.IsValid() || !Value->TryGetObject(Obj) || !Obj || !Obj->IsValid())
		{
			continue;
		}

		FSignEntry E;
		(*Obj)->TryGetStringField(TEXT("gloss"), E.Gloss);
		(*Obj)->TryGetStringField(TEXT("animation"), E.Animation);
		(*Obj)->TryGetBoolField(TEXT("has_animation"), E.bHasAnimation);
		(*Obj)->TryGetBoolField(TEXT("fingerspell"), E.bFingerspell);
		double Duration = 0.0;
		if ((*Obj)->TryGetNumberField(TEXT("duration"), Duration) && Duration > 0.0)
		{
			E.Duration = static_cast<float>(Duration);
		}
		if (E.Gloss.IsEmpty())
		{
			continue;
		}
		Glosses.Add(E.Gloss);
		Queue.Add(E);
	}

	UE_LOG(LogSign, Display, TEXT("Received \"%s\" -> %s"), *Original, *FString::Join(Glosses, TEXT(" ")));
}

// =====================================================================================
// Playback
// =====================================================================================

UAnimInstance* ASignReceiver::GetAnim()
{
	if (!Body.IsValid() && Avatar)
	{
		TArray<USkeletalMeshComponent*> Comps;
		Avatar->GetComponents(Comps);
		for (USkeletalMeshComponent* C : Comps)
		{
			if (C && C->GetFName() == BodyComponentName)
			{
				Body = C;
				break;
			}
		}
		if (!Body.IsValid() && !bWarnedNoAnim)
		{
			bWarnedNoAnim = true;
			UE_LOG(LogSign, Warning, TEXT("SignReceiver: Avatar has no skeletal mesh component named '%s'."),
				*BodyComponentName.ToString());
		}
	}

	UAnimInstance* Anim = Body.IsValid() ? Body->GetAnimInstance() : nullptr;
	if (!Anim && Body.IsValid() && !bWarnedNoAnim)
	{
		bWarnedNoAnim = true;
		UE_LOG(LogSign, Warning, TEXT("SignReceiver: the Body has no Animation Blueprint. Set Animation Mode = Use Animation Blueprint, Anim Class = ABP_Signer."));
	}
	return Anim;
}

const FSignClip* ASignReceiver::FindClip(const FString& Key) const
{
	const FString K = Key.TrimStartAndEnd().ToUpper();
	if (K.IsEmpty())
	{
		return nullptr;
	}
	for (const TPair<FString, FSignClip>& Pair : SignClips)
	{
		if (Pair.Value.Anim && Pair.Key.TrimStartAndEnd().ToUpper() == K)
		{
			return &Pair.Value;
		}
	}
	return nullptr;
}

void ASignReceiver::PlayClip(UAnimInstance* Anim, const FSignClip& Clip, float BlendIn)
{
	if (!Anim || !Clip.Anim)
	{
		return;
	}

	// Freeze the old sign on its last real pose while the new one blends in, so it never
	// drifts back towards rest during the blend.
	if (CurrentMontage)
	{
		Anim->Montage_Pause(CurrentMontage);
	}

	const float Len = Clip.Anim->GetPlayLength();
	const float InSec = FMath::Clamp(Clip.InPoint, 0.f, 1.f) * Len;
	float OutSec = FMath::Clamp(Clip.OutPoint, Clip.InPoint, 1.f) * Len;
	OutSec = FMath::Min(FMath::Max(OutSec, InSec + 0.05f), FMath::Max(Len - 0.02f, InSec));

	CurrentRate = Clip.PlayRate;
	CurrentOutSeconds = OutSec;

	// Smooth (ease-in/ease-out) blends instead of linear ones.
	FAlphaBlendArgs In(BlendIn);
	In.BlendOption = EAlphaBlendOption::HermiteCubic;
	FAlphaBlendArgs Out(ToRestBlend);
	Out.BlendOption = EAlphaBlendOption::HermiteCubic;

	// BlendOutTriggerTime 0 = only blend out at the very end; we pause before that ourselves.
	CurrentMontage = Anim->PlaySlotAnimationAsDynamicMontage_WithBlendArgs(
		Clip.Anim, SlotName, In, Out, CurrentRate * Tempo, 1, 0.f, InSec);
}

void ASignReceiver::GoToRest(UAnimInstance* Anim, float Blend)
{
	if (Anim && CurrentMontage)
	{
		Anim->Montage_Stop(Blend, CurrentMontage);
	}
	CurrentMontage = nullptr;
	State = EState::Rest;
}

void ASignReceiver::StartNext(UAnimInstance* Anim)
{
	const FSignEntry E = Queue[0];
	Queue.RemoveAt(0);
	OnSignStarted(E);

	const bool bAtRest = (CurrentMontage == nullptr);
	const float Blend = bAtRest ? FromRestBlend : SignToSignBlend;

	// 1) A clip for this gloss (or its animation name)?
	const FSignClip* Clip = FindClip(E.Gloss);
	if (!Clip)
	{
		Clip = FindClip(E.Animation);
	}
	if (Clip && Anim)
	{
		UE_LOG(LogSign, Display, TEXT("> %s"), *E.Gloss);
		PlayClip(Anim, *Clip, Blend);
		State = EState::Playing;
		return;
	}

	// 2) Fingerspelling, if every letter has a clip (FS_A, FS_B, ...).
	if (E.bFingerspell && Anim)
	{
		SpellClips.Reset();
		bool bAllLetters = true;
		for (const TCHAR C : E.Gloss.ToUpper())
		{
			if (!FChar::IsAlpha(C))
			{
				continue;
			}
			const FSignClip* Letter = FindClip(LetterPrefix + FString::Chr(C));
			if (!Letter)
			{
				bAllLetters = false;
				break;
			}
			SpellClips.Add(*Letter);
		}
		if (bAllLetters && SpellClips.Num() > 0)
		{
			UE_LOG(LogSign, Display, TEXT("~ %s (fingerspelled)"), *E.Gloss);
			LetterIndex = 0;
			PlayClip(Anim, SpellClips[0], Blend);
			StateTimer = SecondsPerLetter + Blend;
			State = EState::Spelling;
			return;
		}
	}

	// 3) No clip: back to the idle and wait as long as Python says.
	UE_LOG(LogSign, Display, TEXT("- %s (no clip, %.2f s)"), *E.Gloss, E.Duration);
	GoToRest(Anim, PauseBlend);
	StateTimer = E.Duration;
	State = EState::Pausing;
}

void ASignReceiver::TickPlayback(float DeltaSeconds)
{
	// Tempo follows the backlog, but changes slowly.
	const float K = FMath::Clamp((Queue.Num() - 1.f) / FMath::Max(1.f, BacklogForMaxTempo - 1.f), 0.f, 1.f);
	Tempo = FMath::FInterpConstantTo(Tempo, FMath::Lerp(1.f, MaxTempo, K), DeltaSeconds, 0.4f);

	UAnimInstance* Anim = GetAnim();

	switch (State)
	{
	case EState::Rest:
	case EState::Holding:
		if (Queue.Num() > 0)
		{
			StartNext(Anim);
		}
		else if (State == EState::Holding)
		{
			StateTimer += DeltaSeconds;
			if (StateTimer >= HoldBeforeRest)
			{
				GoToRest(Anim, ToRestBlend);
			}
		}
		break;

	case EState::Playing:
		if (!Anim || !CurrentMontage || !Anim->Montage_IsActive(CurrentMontage))
		{
			State = EState::Holding;
			StateTimer = 0.f;
			break;
		}
		Anim->Montage_SetPlayRate(CurrentMontage, CurrentRate * Tempo);
		if (Anim->Montage_GetPosition(CurrentMontage) >= CurrentOutSeconds)
		{
			if (Queue.Num() > 0)
			{
				StartNext(Anim);   // straight into the next sign
			}
			else
			{
				Anim->Montage_Pause(CurrentMontage);   // hold the last pose
				State = EState::Holding;
				StateTimer = 0.f;
			}
		}
		break;

	case EState::Pausing:
		StateTimer -= DeltaSeconds * Tempo;
		if (StateTimer <= 0.f)
		{
			State = EState::Holding;
			StateTimer = HoldBeforeRest;   // already at rest: no extra hold
		}
		break;

	case EState::Spelling:
		StateTimer -= DeltaSeconds * Tempo;
		if (StateTimer <= 0.f)
		{
			if (++LetterIndex < SpellClips.Num())
			{
				PlayClip(Anim, SpellClips[LetterIndex], LetterBlend);
				StateTimer = SecondsPerLetter;
			}
			else
			{
				if (Anim && CurrentMontage)
				{
					Anim->Montage_Pause(CurrentMontage);
				}
				State = EState::Holding;
				StateTimer = 0.f;
			}
		}
		break;
	}

	const bool bBusy = Queue.Num() > 0 || State == EState::Playing || State == EState::Pausing || State == EState::Spelling;
	if (bWasBusy && !bBusy)
	{
		OnQueueEmpty();
	}
	bWasBusy = bBusy;
}
