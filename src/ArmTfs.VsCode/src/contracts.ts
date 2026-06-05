export interface JsonEnvelope {
  schemaVersion: number;
  command: string;
}

export interface IdentityRef {
  displayName?: string;
  uniqueName?: string;
}

export interface WorkspaceMapping {
  serverPath: string;
  localPath: string;
}

export interface WorkspaceInfo {
  name: string;
  owner?: string;
  serverCollectionUrl: string;
  mappings: WorkspaceMapping[];
}

export interface StatusItem {
  state: string;
  changeType?: string;
  serverPath: string;
  localPath: string;
  addedAt?: string;
  sourceServerPath?: string;
  baselineChangesetId?: number;
  downloadedAt?: string;
  trackedChangesetId?: number;
}

export interface StatusResponse extends JsonEnvelope {
  command: 'status';
  workspace: WorkspaceInfo;
  scope: {
    inputPath: string;
    resolvedLocalPath: string;
  };
  items: StatusItem[];
}

export interface HistoryItem {
  changesetId: number;
  createdAt: string;
  comment?: string;
  commentTruncated?: boolean;
  author?: IdentityRef;
  checkedInBy?: IdentityRef;
}

export interface HistoryResponse extends JsonEnvelope {
  command: 'history';
  query: {
    inputPath: string;
    serverPath?: string;
    author?: string;
    top: number;
  };
  items: HistoryItem[];
}

export interface DiffResponse extends JsonEnvelope {
  command: 'diff';
  target: {
    inputPath: string;
    localPath: string;
    serverPath: string;
  };
  compareTo: {
    mode: 'latest' | 'base' | 'changeset';
    changesetId?: number;
  };
  workspaceState: {
    state: string;
    changeType?: string;
    trackedChangesetId?: number;
  };
  result: {
    kind: 'none' | 'binary' | 'text';
    localSize: number;
    serverSize: number;
    patch?: string;
  };
}

export interface BranchRef {
  path: string;
  description?: string;
  owner?: IdentityRef;
  createdAt?: string;
  isDeleted: boolean;
}

export interface BranchListResponse extends JsonEnvelope {
  command: 'branch.list';
  query: {
    scope: string;
    includeDeleted: boolean;
  };
  items: BranchRef[];
}

export interface BranchShowResponse extends JsonEnvelope {
  command: 'branch.show';
  branch: {
    path: string;
    description?: string;
    owner?: IdentityRef;
    createdAt?: string;
    isDeleted: boolean;
    parentPath?: string;
    children?: string[];
    relatedBranches?: string[];
    mappings?: Array<{
      serverItem: string;
      type: string;
      depth: string;
    }>;
  };
}

export interface ChangesetShowResponse extends JsonEnvelope {
  command: 'changeset.show';
  changeset: {
    changesetId: number;
    createdAt: string;
    comment?: string;
    commentTruncated?: boolean;
    author?: IdentityRef;
    checkedInBy?: IdentityRef;
    hasMoreChanges?: boolean;
    changes?: Array<{
      changeType: string;
      item?: {
        path: string;
        changesetVersion?: number;
        deletionId?: number;
        isBranch?: boolean;
        changeDate?: string;
        size?: number;
        hashValue?: string;
      };
      pendingVersion?: number;
      mergeSources?: Array<{
        serverItem: string;
        versionFrom?: number;
        versionTo?: number;
        isRename?: boolean;
      }>;
    }>;
    workItems?: Array<{
      id: number;
      title?: string;
      state?: string;
      assignedTo?: string;
      workItemType?: string;
      url?: string;
    }>;
  };
}

export interface LabelListResponse extends JsonEnvelope {
  command: 'label.list';
  query: {
    owner?: string;
    name?: string;
    scope?: string;
    top: number;
    skip: number;
  };
  items: Array<{
    id: number;
    name: string;
    description?: string;
    labelScope?: string;
    modifiedDate?: string;
    owner?: IdentityRef;
  }>;
}

export interface LabelShowResponse extends JsonEnvelope {
  command: 'label.show';
  label: {
    id: number;
    name: string;
    description?: string;
    labelScope?: string;
    modifiedDate?: string;
    owner?: IdentityRef;
    items?: Array<{
      path: string;
      changesetVersion?: number;
      isBranch?: boolean;
      deletionId?: number;
      changeDate?: string;
      size?: number;
      hashValue?: string;
    }>;
  };
}

export interface MergeBasePayload {
  sourcePath: string;
  targetPath: string;
  sourceBranchPath?: string;
  targetBranchPath?: string;
  sourceAncestry: string[];
  targetAncestry: string[];
  commonAncestorPath?: string;
  relationship: string;
  sourceBranchCreatedAt?: string;
  targetBranchCreatedAt?: string;
  sourceBranchPointChangesetId?: number;
  targetBranchPointChangesetId?: number;
  confidence: string;
  notes: string[];
}

export interface MergeBaseResponse extends JsonEnvelope {
  command: 'merge.base';
  query: {
    sourcePath: string;
    targetPath: string;
    inferenceMode: 'branch-history';
  };
  mergeBase: MergeBasePayload;
}

export interface MergeCandidateResponse extends JsonEnvelope {
  command: 'merge.candidate';
  query: {
    sourcePath: string;
    targetPath: string;
    top: number;
    scan: number;
    inferenceMode: 'branch-history';
  };
  mergeBase: MergeBasePayload;
  summary: {
    sourceHistoryScanned: number;
    targetHistoryScanned: number;
    sourceUniqueFloorChangesetId?: number;
    mergedRangesCount: number;
  };
  items: Array<{
    changesetId: number;
    createdAt: string;
    comment?: string;
    author?: IdentityRef;
    isMergedToTarget: boolean;
    coveredByTargetChangesetId?: number;
    coveredByRange?: {
      serverItem: string;
      versionFrom?: number;
      versionTo?: number;
      isRename?: boolean;
      targetChangesetId: number;
    };
  }>;
}

export interface ServerItemEntry {
  serverPath: string;
  isFolder: boolean;
  changesetId: number;
  contentLength?: number;
  checkinDate?: string;
}

export interface ItemsListResponse extends JsonEnvelope {
  command: 'items.list';
  query: {
    path: string;
    recursive: boolean;
  };
  items: ServerItemEntry[];
}